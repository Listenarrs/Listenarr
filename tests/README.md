# Tests

This project is used to test Listenarr backend

### Project Structure

```
tests/                              # Backend tests
├── Builders/                       # Data creation helpers using the builder pattern
├── Common/                         # Underlying test framework
│   ├── BaseMock.cs                 # Base class for API mocks
│   ├── BaseTests.cs                # Base class for all tests
│   └── MockUtils.cs                # Helper methods for mocks
├── Features/                       # Main directory for tests files
│   ├── Api/                        # listenarr.api tests
│   ├── Application/                # listenarr.application tests
│   ├── Domain/                     # listenarr.domain tests
│   └── Infrastructure/             # listenarr.infrastructure tests
├── Mocks/                          # Mock definitions
│   └── Api/                        # API mock definitions
├── Listenarr.BackendIntegrationTests/ # The whole app in process, driven through its HTTP API
├── Listenarr.TestHost/             # Fixture-backed stub of the external APIs (also the E2E test host)
├── Listenarr.Tests.csproj          # Testing project
└── README.md                       # Backend tests documentation
```

### Backend integration tests

`Listenarr.BackendIntegrationTests/` is a separate project: each test starts the whole application in process
(`WebApplicationFactory`) on its own SQLite file and temporary root folders, seeds four books and two root folders
through the API, and then uses only HTTP requests, as the frontend does. External APIs answer from the TestHost
fixtures; the import scan reads real tags with the `ffprobe` found on PATH (install FFmpeg). The project builds on
every OS. In CI it runs in its own Linux job, `backend-integration-tests`; the native backend jobs skip it
(it is a test project in CI only when `ListenarrBackendIntegrationJob=true`).

```
dotnet test tests/Listenarr.BackendIntegrationTests
```

### Creating a test

* Test name should be the name of the class you are trying to test followed by `Tests`. Example for `DownloadService`: `DownloadServiceTests`
* Your test class should be in a path mirroring the main project being tested by it (example: `Adaper` tests are in `Features/Api/Services/Adapters`)
* Your test class should inherits `BaseTests`. Example for `DownloadService`: `DownloadServiceTests : BaseTests`
* Your test class should define the following tags:
```
[Trait("Name", "DownloadServiceTests")]
[Trait("Category", "DownloadService")]
```
* You can override `InitializeAsync` to define common setup for all test methods in your test class
* You can override or remove services for dependency injection by calling `Init()` at the start of the test or test setup:
```
Init(services => services.WithSingleton(myMock.Object));
Init(services => services.Without<IServiceToRemove>());
```

* If you need a custom service lifetime, use the matching method in the `Init()` builder
```
Init(services => services.WithScoped<IMyService, MyService>());
```
* Take note of how the initial class was registered as it might not be obvious how some of them should be replaced to make sure the test actually uses it
* You should add data to repositories only after you have called `Init()` with any custom service overrides
* If you did not alter any service, `Init()` is done for you in the test constructor already

The `Init()` method create the following things:
* A `ServiceProvider` using the services you injected and default ones added for you. This provider is accessible using `_provider`
* A reference to the most useful repositories to save you some line of codes:
    * `IDownloadRepository` under `_downloadRepository`
    * `IDownloadClientConfigurationRepository` under `_downloadClientConfigurationRepository`
    * And so on...

Tests cases should be defined using the following steps:
* Given: Initial data/situation
* When: Action to be done/tested
* Then: Assertions to make sure the situation is as expected

### Creating a mock
* API mock should inherits `BaseMocks`
* Any mock inheriting `BaseMocks` has access to:
    * `GetCallCount()` to know how many times the mock was used
    * `GetLastRequest()` to get the latest request processed by the mock
    * `GetLastContent()` to get the latest request body processed by the mock
    * And some other helpful things, check the code for more informations

### Initializing data

* Data should be initialized using the builder pattern
* Each data builder should produce actionable and coherent data (meaning, all mandatory field have plausible value, interdependant fields are populated and so on)

### Dependency Injection (DI)

* Each test class is responsible for defining the mock it wants to use
* Mandatory mocks are the ones that interfaces with external interfaces (search providers, download client adapters through, mostly, http clients, ...)
