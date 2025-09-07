using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Text.RegularExpressions;

// Build host for DI and configuration
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("Database") 
        ?? Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("Database connection string not found");
    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddHttpClient();
builder.Services.AddTransient<WeatherScrapingService>();

var host = builder.Build();

// Initialize database
using (var scope = host.Services.CreateScope())
{
    var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    await InitializeDatabaseAsync(dataSource);
}

// Run the job once and exit
var weatherService = host.Services.GetRequiredService<WeatherScrapingService>();
await weatherService.ExecuteAsync();

Console.WriteLine("Weather scraping completed. Exiting.");

public class WeatherScrapingService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WeatherScrapingService> _logger;

    public WeatherScrapingService(
        NpgsqlDataSource dataSource, 
        IHttpClientFactory httpClientFactory, 
        ILogger<WeatherScrapingService> logger)
    {
        _dataSource = dataSource;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        _logger.LogInformation("Starting weather scraping job at {time}", DateTime.Now);

        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var dateString = yesterday.ToString("yyyy-MM-dd");

            var station1Url = $"https://www.wunderground.com/dashboard/pws/KWABLAIN153/table/{dateString}/{dateString}/daily";
            var station2Url = $"https://www.wunderground.com/dashboard/pws/KWABLAIN126/table/{dateString}/{dateString}/daily";

            _logger.LogInformation("Collecting weather station data for {date}", dateString);

            // Scrape weather stations
            var tasks = new[]
            {
                ScrapeWeatherStation(station1Url, "KWABLAIN153", yesterday),
                ScrapeWeatherStation(station2Url, "KWABLAIN126", yesterday)
            };

            var results = await Task.WhenAll(tasks);
            
            // Scrape forecast
            _logger.LogInformation("Collecting forecast data...");
            await ScrapeForecast();

            _logger.LogInformation("Weather scraping completed successfully at {time}", DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Weather scraping failed");
            Environment.Exit(1); // Exit with error code for cron monitoring
        }
    }

    private async Task<bool> ScrapeWeatherStation(string url, string stationId, DateTime date)
    {
        // Check if data exists
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM weather_stations WHERE station_id = @id AND timestamp::date = @date LIMIT 1", 
            connection);
        checkCmd.Parameters.AddWithValue("id", stationId);
        checkCmd.Parameters.AddWithValue("date", date.Date);
        
        if (await checkCmd.ExecuteScalarAsync() != null)
        {
            _logger.LogInformation("Data for {station} on {date} already exists, skipping", stationId, date.Date);
            return false;
        }

        // Scrape data
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/91.0.4472.124 Safari/537.36");

        try
        {
            _logger.LogInformation("Scraping {station} from {url}", stationId, url);
            var html = await httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("//table[contains(@class, 'history-table')]//tbody//tr");
            if (rows == null) 
            {
                _logger.LogWarning("No table rows found for {station}", stationId);
                return false;
            }

            int savedCount = 0;
            foreach (var row in rows)
            {
                var cells = row.SelectNodes("td");
                if (cells?.Count < 4) continue;

                var timeText = cells[0].InnerText.Trim();
                var tempText = cells[1].InnerText.Replace("°F", "").Trim();
                var humidityText = cells[3].InnerText.Replace("%", "").Trim();

                if (decimal.TryParse(tempText, out var temp) &&
                    int.TryParse(humidityText, out var humidity) &&
                    TimeSpan.TryParse(timeText, out var time))
                {
                    var timestamp = date.Date.Add(time);
                    
                    await using var insertCmd = new NpgsqlCommand(@"
                        INSERT INTO weather_stations (station_id, timestamp, temperature, humidity)
                        VALUES (@id, @timestamp, @temp, @humidity)", connection);
                    
                    insertCmd.Parameters.AddWithValue("id", stationId);
                    insertCmd.Parameters.AddWithValue("timestamp", timestamp);
                    insertCmd.Parameters.AddWithValue("temp", temp);
                    insertCmd.Parameters.AddWithValue("humidity", humidity);
                    
                    await insertCmd.ExecuteNonQueryAsync();
                    savedCount++;
                }
            }

            _logger.LogInformation("Scraped {count} readings for {station}", savedCount, stationId);
            return savedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape {station}", stationId);
            return false;
        }
    }

    private async Task ScrapeForecast()
    {
        // Check if forecast exists for today
        var today = DateTime.Today;
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM forecasts WHERE timestamp = @timestamp LIMIT 1", connection);
        checkCmd.Parameters.AddWithValue("timestamp", today);
        
        if (await checkCmd.ExecuteScalarAsync() != null)
        {
            _logger.LogInformation("Forecast for today already exists, skipping");
            return;
        }

        _logger.LogInformation("Collecting 10-day forecast data...");

        // Setup Chrome in headless mode
        var options = new ChromeOptions();
        options.AddArguments("--headless", "--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu");
        
        using var driver = new ChromeDriver(options);
        int totalSaved = 0;

        try
        {
            for (int i = 1; i <= 10; i++)
            {
                var forecastDate = today.AddDays(i);
                var url = $"https://www.wunderground.com/hourly/us/wa/blaine/48.99,-122.75/date/{forecastDate:yyyy-MM-dd}";
                
                try
                {
                    _logger.LogInformation("Scraping forecast for {date}", forecastDate.Date);
                    driver.Navigate().GoToUrl(url);
                    
                    // Wait for table to load
                    await Task.Delay(3000);

                    var tableElement = driver.FindElement(By.Id("hourly-forecast-table"));
                    var tableHtml = tableElement.GetAttribute("outerHTML");

                    var doc = new HtmlDocument();
                    doc.LoadHtml(tableHtml);

                    var rows = doc.DocumentNode.SelectNodes("//tbody//tr");
                    if (rows == null) continue;

                    int daySaved = 0;
                    foreach (var row in rows)
                    {
                        var cells = row.SelectNodes("td");
                        if (cells?.Count < 9) continue;

                        var timeCellText = cells[0].InnerText.Trim();
                        var timeStr = ParseTime(timeCellText);
                        
                        var forecastDateTime = forecastDate.Date.Add(TimeSpan.Parse($"{timeStr}:00"));
                        
                        if (int.TryParse(cells[2].InnerText.Replace("°", "").Trim(), out var temp) &&
                            int.TryParse(cells[4].InnerText.Replace("%", "").Trim(), out var precip) &&
                            int.TryParse(cells[8].InnerText.Replace("%", "").Trim(), out var humidity))
                        {
                            await using var insertCmd = new NpgsqlCommand(@"
                                INSERT INTO forecasts (timestamp, forecast_date, temp, precip, humidity)
                                VALUES (@timestamp, @forecastDate, @temp, @precip, @humidity)", connection);
                            
                            insertCmd.Parameters.AddWithValue("timestamp", today);
                            insertCmd.Parameters.AddWithValue("forecastDate", forecastDateTime);
                            insertCmd.Parameters.AddWithValue("temp", temp);
                            insertCmd.Parameters.AddWithValue("precip", precip);
                            insertCmd.Parameters.AddWithValue("humidity", humidity);
                            
                            await insertCmd.ExecuteNonQueryAsync();
                            daySaved++;
                        }
                    }
                    totalSaved += daySaved;
                    _logger.LogInformation("Saved {count} forecast entries for {date}", daySaved, forecastDate.Date);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scrape forecast for {date}", forecastDate.Date);
                }
            }

            _logger.LogInformation("Forecast scraping completed. Total saved: {total}", totalSaved);
        }
        finally
        {
            driver.Quit();
        }
    }

    private static string ParseTime(string cellText)
    {
        var match = Regex.Match(cellText, @"(\d{1,2})\s*:?\s*(\d{2})?\s*(am|pm)", RegexOptions.IgnoreCase);
        if (!match.Success) return "00:00";

        var hour = int.Parse(match.Groups[1].Value);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var ampm = match.Groups[3].Value.ToLower();

        if (ampm == "pm" && hour != 12) hour += 12;
        if (ampm == "am" && hour == 12) hour = 0;

        return $"{hour:00}:{minute:00}";
    }
}

static async Task InitializeDatabaseAsync(NpgsqlDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync();

    var createTables = new[]
    {
        @"CREATE TABLE IF NOT EXISTS weather_stations (
            id SERIAL PRIMARY KEY,
            station_id VARCHAR(20) NOT NULL,
            timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            temperature DECIMAL(5,2),
            humidity INTEGER,
            wind_speed DECIMAL(5,2),
            wind_direction VARCHAR(10),
            pressure DECIMAL(6,2),
            precipitation DECIMAL(5,2),
            conditions VARCHAR(100)
        )",
        @"CREATE TABLE IF NOT EXISTS forecasts (
            id SERIAL PRIMARY KEY,
            timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            forecast_date TIMESTAMP,
            temp INTEGER,
            precip INTEGER,
            humidity INTEGER
        )",
        @"CREATE TABLE IF NOT EXISTS weather_comparisons (
            id SERIAL PRIMARY KEY,
            timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            station_1_temp DECIMAL(5,2),
            station_2_temp DECIMAL(5,2),
            temp_difference DECIMAL(5,2),
            station_1_humidity INTEGER,
            station_2_humidity INTEGER,
            humidity_difference INTEGER,
            station_1_wind DECIMAL(5,2),
            station_2_wind DECIMAL(5,2),
            wind_difference DECIMAL(5,2)
        )"
    };

    foreach (var sql in createTables)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}