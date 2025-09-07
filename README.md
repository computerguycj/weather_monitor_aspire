
# Weather Monitor Aspire

## Overview
Weather Monitor Aspire is a .NET 8.0 console application that scrapes weather data and forecasts from Weather Underground for specific stations, stores the data in a PostgreSQL database, and supports automated scheduled runs. It uses Selenium, HtmlAgilityPack, and Npgsql for scraping and data management.

## Features
- Scrapes historical weather data from multiple stations
- Collects 10-day weather forecasts
- Stores data in PostgreSQL
- Uses headless Chrome for dynamic content scraping
- Skips duplicate data entries
- Logging and error handling for automation

## Requirements
- .NET 8.0 SDK
- PostgreSQL database
- Chrome browser (for Selenium)
- ChromeDriver (compatible with installed Chrome version)

## Setup
1. Clone the repository:
	```sh
	git clone <your-repo-url>
	cd weather_monitor_aspire
	```
2. Configure your database connection string in `appsettings.json` or via the `DATABASE_URL` environment variable.
3. Restore dependencies:
	```sh
	dotnet restore
	```
4. Build the project:
	```sh
	dotnet build
	```
5. Run the application:
	```sh
	dotnet run --project weather_monitor_aspire
	```

## Usage
The application will scrape weather data for the previous day and a 10-day forecast, then store the results in your PostgreSQL database. Schedule it with a task scheduler (e.g., cron, Windows Task Scheduler) for automation.

## Project Structure
- `weather_monitor.cs` - Main application logic
- `weather_monitor.csproj` - Project file and dependencies
- `.gitignore` - Ignores build output and temp files

## Contributing
Pull requests are welcome! For major changes, please open an issue first to discuss what you would like to change.

## License
MIT License. See `LICENSE` file for details.

## Contact
Maintained by CJohnsoni3verticals. For questions, open an issue or contact via GitHub.
