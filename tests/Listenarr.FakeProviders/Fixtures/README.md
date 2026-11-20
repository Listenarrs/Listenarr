# Metadata provider fixtures

Everything that runs Listenarr for tests (the `Listenarr.FakeProviders` program in the E2E and backend
integration container, `Listenarr.BackendIntegrationTests`, and the `Listenarr.Tests` containers through
`AddListenarrExternalApiTestStub`) answers the metadata providers from these files instead of the
network. Each provider has a folder here and a route class in `../Routes/` that maps a request to a file
name; the class's summary lists its routes and naming rules.

| Provider    | Hosts                                       | Routes                                  |
|-------------|---------------------------------------------|-----------------------------------------|
| Audible     | `api.audible.{tld}`, `www.audible.{tld}`    | `Routes/AudibleRoutes.cs`               |
| Audnexus    | `api.audnex.us`                             | `Routes/AudnexusRoutes.cs`              |
| OpenLibrary | `openlibrary.org`, `covers.openlibrary.org` | `Routes/OpenLibraryRoutes.cs`           |

A request without a fixture gets a 404 and a line on stderr naming the file that would answer it:

```
External API stub: no fixture for GET https://api.audnex.us/books/B008DFUGCQ?region=us&... -> record as Audnexus/book-B008DFUGCQ.json
```

To record the missing ones from the real APIs, collect those lines in a file and replay them:

```
LISTENARR_TEST_MISSING_FIXTURES=/tmp/missing.log dotnet test tests/Listenarr.Tests.csproj --filter <test>
node scripts/test-fixtures/record-provider-fixtures.mjs /tmp/missing.log
```

or one URL at a time: `node scripts/test-fixtures/record-provider-fixtures.mjs --url <url> Audible/<file>`.
Review what was recorded before committing it (tests assert on it), and trim it to what the tests need.

A provider without a folder yet has no fixture: every request to it gets the 404. Adding a provider means
a new `ProviderRoutes` class, registered in `ExternalApiStubHandler.Providers`, and a folder here.
Hand-written answers (an empty result for a scenario) stay listed in the route class that serves them,
like `AudibleRoutes.SearchScenarios`.
