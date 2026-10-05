# ⚔️ Kings of the Card Arena

A high-performance, full-stack **Turn-Based Deck Strategy Web Game** built using **.NET Core, Blazor WebAssembly, and Clean Architecture**. This project serves as a production-grade portfolio piece, showcasing an enterprise-level distributed architecture featuring local cloud emulation, containerized infrastructure, and asynchronous event-driven message queuing.

---

## 🏗️ Architectural Overview

This system follows **Clean Architecture** patterns to enforce a strict separation of concerns, decoupling the core domain logic from framework dependencies, web APIs, and database engines.

```
📁 GamePortfolioSolution
│
├── 📁 1.Core (Enterprise Domain Rules & Logic)
│     └── Pure C# models, entities, and services. Zero dependencies.
│
├── 📁 2.Infrastructure (Data & Cloud Service Operations)
│     └── EF Core DBContext, Fluent Configurations, and Cloud Storage SDKs.
│
├── 📁 3.BackendAPI (High-Performance Orchestration Layer)
│     └── Minimal APIs, CORS, and native asynchronous Hosted Workers.
│
└── 📁 4.Frontend (Stateful Web Interface Engine)
      └── Blazor WebAssembly single-page application and unified state container.
```

### ⚡ Technical Infrastructure Matrix

The local environment mimics an enterprise ecosystem entirely through **Docker Compose**, running light-weight, decoupled open-source software nodes:

*   **Frontend UI Engine:** Blazor WebAssembly (SPA client) running on a native runtime host thread.
*   **Web API Routing Gateway:** ASP.NET Core Minimal APIs utilizing lightweight dependency mapping pipelines.
*   **Relational Database Context:** **Microsoft SQL Server 2022** container managing transaction persistence over port `1433`.
*   **Cloud Object Storage Warehouse:** **Azure Azurite Emulator** managing avatar streaming uploads on port `10000`.
*   **Asynchronous Message Broker:** **RabbitMQ Engine** handling thread offloading decoupling over port `5672`.

---

## 🛠️ Complete Local Machine Setup

Follow these sequential steps to clone, configure, and boot the entire application cluster locally.

### 📋 Prerequisites
Ensure your development computer has the following tools installed:
*   [.NET 8.0 SDK (or later)](https://dotnet.microsoft.com/download)
*   [Docker Desktop](https://www.docker.com/products/docker-desktop/)
*   [Entity Framework Core CLI Tools](https://learn.microsoft.com/en-us/ef/core/cli/dotnet) (`dotnet tool install --global dotnet-ef`)

### 1. Initialize Containerized Infrastructure
Navigate to the root directory where your `docker-compose.yaml` file lives and boot up your isolated container cluster:

```bash
docker compose up -d
```

To verify all nodes are running safely, verify their status using:
```bash
docker ps
```
*Expected Output: `game-sql-server`, `game-azurite`, and `game-rabbitmq` will display an **Up** status.*

### 2. Apply Relational Database Schema Migrations
Compile your code layer dependencies and run the Entity Framework Core migrations script. This safely logs into your SQL container, constructs the `GameDb` database context, and builds out the physical tables (`Players`, `Matches`) automatically:

```bash
dotnet ef database update --project 2.Infrastructure/Game.Infrastructure/Game.Infrastructure.csproj --startup-project 3.BackendAPI/Game.Api/Game.Api.csproj
```

<h3>3. Start the Backend API Services</h3>
Run the Web API runtime routing channel directly from the root folder:

```bash
dotnet run --project 3.BackendAPI/Game.Api/Game.Api.csproj
```
*Note: Take note of the application's hosting URL from the logs (e.g., `http://localhost:5005`). You can visit `/swagger` on this port to interact directly with the live endpoints.*

### 4. Boot up the Blazor Web Interface
Open a separate terminal window and launch your client single-page web game shell:

```bash
dotnet run --project 4.Frontend/Game.Client/Game.Client.csproj
```
Click the local port link printed out to open the unified browser window client panel!

---

## 🎨 Core Architectural Highlights

### 🛡️ Clean Domain Modeling & Rich Encapsulation
Unlike standard anemic models built using basic read/write `get; set;` templates, the domain entities (`Player.cs`, `GameMatch.cs`) use explicit **encapsulation rules**. State mutation paths (like `DeductGold` or `AddExperience`) are locked inside domain boundary methods, ensuring that validation and business mechanics are completely untainted by external layers.

### 📨 Frameworkless Asynchronous Event Distribution
To ensure immediate execution responsiveness, the system completely separates the match completion thread. When a match wraps up, the Minimal API drops an immutable record onto your **RabbitMQ** broker channel in milliseconds and exits immediately. 

A native, free .NET **`BackgroundService` Hosted Worker** (`MatchConsumerWorker.cs`) monitors port `5672` on a persistent background thread, pulls the data packets down safely, computes rewards via the `MatchRulesEngine`, and writes the newly awarded stats permanently to SQL Server.

### ☁️ Hybrid Object Storage Architecture
Raw images are never stored directly in the relational database. When a hero customization avatar is committed, the binary stream maps down port `10000` directly into your containerized **`game-azurite` Docker Container**. Once written successfully, a lightweight string pointer URL text file parameter is recorded onto the SQL database row. On subsequent page refreshes, the user's browser loads the image via a direct link, keeping database memory thin and performant.

### 🧬 Reactive UI State Containers
The Blazor WebAssembly frontend eliminates jarring web page jumps and route parameter pass parameters. The user interface uses a scoped single-instance **`GameState.cs` Service Container** acting as a live client state manager. Views (Login, Dashboard, Combat Canvas) swap instantly in response to real-time events, providing a zero-latency, application-like experience.

---

## 📈 Next Milestones
*   **Ranked Leaderboard Canvas:** Implement an optimized SQL index query to pull and display top heroes sorted by level on a visual card roster board.
*   **Automated xUnit Test Harness:** Implement a robust test layer to execute verification routines protecting combat calculations and gold deduction validation constraints.
