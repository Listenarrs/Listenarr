# Tests

This project is used to test Listenarr backend

### Project Structure

```
tests/                              # Backend tests
├── Builders/                       # Data creation helpers using the builder pattern
├── Common/                         # Underlying test framework
│   ├── BaseApiMock.cs              # Base class for API mocks
│   ├── BaseTests.cs                # Base class for all tests
│   ├── CoverageInstrumentation.cs  # Time limits that allow for coverage instrumentation
│   └── MockUtils.cs                # Helper methods for mocks
├── Features/                       # Main directory for tests files
│   ├── Api/                        # listenarr.api tests
│   ├── Application/                # listenarr.application tests
│   ├── Domain/                     # listenarr.domain tests
│   └── Infrastructure/             # listenarr.infrastructure tests
├── Mocks/                          # Mock definitions (metadata providers are faked by Listenarr.FakeProviders, not here)
│   ├── DownloadClients/            # SABnzbd, NZBGet, qBittorrent, Transmission API mocks, adapter and gateway mocks
│   ├── Trackers/                   # Tracker API mocks (MyAnonamouse)
│   ├── Hosting/                    # WebApplicationFactory and startup configuration
│   ├── Media/                      # FFmpeg
│   ├── Metadata/                   # Metadata service (in-process, not the providers' HTTP APIs)
│   └── Realtime/                   # SignalR hub broadcaster
├── Listenarr.BackendIntegrationTests/ # The whole app in process, driven through its HTTP API
├── Listenarr.FakeProviders/        # Fake metadata providers, and the program running Listenarr with them (E2E, backend integration)
│   ├── Routes/                     # One route class per provider (Audible, Audnexus, OpenLibrary)
│   └── Fixtures/                   # One folder per provider (see its README: naming, recording missing fixtures)
├── Listenarr.Tests.csproj          # Testing project
├── coverage.runsettings            # Backend coverage settings (coverlet, Cobertura)
└── README.md                       # Backend tests documentation
```

### Backend integration tests

`Listenarr.BackendIntegrationTests/` is a separate project: each test starts the whole application in process
(`WebApplicationFactory`) on its own SQLite file and temporary root folders, seeds four books and two root folders
through the API, and then uses only HTTP requests, as the frontend does. External APIs answer from the Listenarr.FakeProviders
fixtures; the import scan reads real tags with the `ffprobe` found on PATH (install FFmpeg). The project builds on
every OS. In CI it runs in its own Linux job, `backend-integration-tests`; the native backend jobs skip it
(it is a test project in CI only when `ListenarrBackendIntegrationJob=true`).

```
dotnet test tests/Listenarr.BackendIntegrationTests
```

### Coverage

Coverage is off by default, locally and in CI. `tests/coverage.runsettings` collects it with coverlet, for
Listenarr's own assemblies only, as Cobertura:

```
dotnet test tests/Listenarr.Tests.csproj --settings tests/coverage.runsettings --results-directory <dir>
```

In CI, a manual run of the `Run Tests` workflow with the `coverage` input collects it in every test job and merges it in a
last `coverage` job (see `fe/README.md`, "Test Coverage"). It is reported, never gated.

Coverlet adds a hit counter to every line of Listenarr's assemblies, so instrumented hot loops run several times slower.
A test that asserts a wall-clock limit on Listenarr code takes it from
`CoverageInstrumentation.TimeLimitMilliseconds(typeof(TestedClass), ms)`, which allows three times the limit when that
class's assembly is instrumented. Fixed waits in concurrency tests that expire under coverage are raised one by one,
with a comment saying why.

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
* Metadata providers (Audible, Audnexus, OpenLibrary) are never mocked by hand: they are answered by
  `Listenarr.FakeProviders`, one route class per provider in `Routes/` and its fixtures in `Fixtures/{Provider}/`.
  A missing fixture is a 404 plus a log line naming the file to add; see `Listenarr.FakeProviders/Fixtures/README.md`
  to record it from the real API with `scripts/test-fixtures/record-provider-fixtures.mjs`.
* Other HTTP APIs (download clients, trackers) are mocked in `Mocks/{Category}/` (`DownloadClients/`, `Trackers/`, ...),
  one namespace per folder. Their API mocks inherit `BaseApiMock` (`Common/BaseApiMock.cs`), which gives:
    * `GetCallCount()` to know how many times the mock was used
    * `GetLastRequest()` to get the latest request processed by the mock
    * `GetLastContent()` to get the latest request body processed by the mock
    * `RequestHistory` and `AddRoute(...)` for per-test routes; check the code for the rest

### Initializing data

* Data should be initialized using the builder pattern
* Each data builder should produce actionable and coherent data (meaning, all mandatory field have plausible value, interdependant fields are populated and so on)

### Dependency Injection (DI)

* Each test class is responsible for defining the mock it wants to use
* Mandatory mocks are the ones that interface with external services (download clients, trackers, mostly through HTTP clients). Metadata providers need none: every test container answers them from `Listenarr.FakeProviders`, and any other production HTTP client gets its 404, so no test reaches the network
