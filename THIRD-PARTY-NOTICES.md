# Third-party notices

Kings of the Card Arena is © 2026 Scott Cox and released under the [MIT License](LICENSE). It ships these third-party files with the game client, each under its own license:

| Component | Version | Where | License |
|---|---|---|---|
| [Bootstrap](https://getbootstrap.com/) | 5.3.3 | `4.Frontend/Game.Client/wwwroot/lib/bootstrap` | MIT, © 2011-2024 The Bootstrap Authors |
| [Application Insights JavaScript SDK](https://github.com/microsoft/ApplicationInsights-JS) | 3.4.5 | `4.Frontend/Game.Client/wwwroot/lib/applicationinsights` | MIT, © Microsoft Corporation ([LICENSE](4.Frontend/Game.Client/wwwroot/lib/applicationinsights/LICENSE)) |

The .NET packages the solution restores from NuGet (ASP.NET Core, Entity Framework Core, OpenTelemetry, the Azure SDKs, RabbitMQ.Client, xUnit, Playwright and others) come with their own licenses, listed on each package's nuget.org page. The test suites use Docker images (SQL Server, Azurite, RabbitMQ) under their publishers' terms; SQL Server's Developer edition is free for development and testing only.
