using System.Net;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    [Trait("Name", "MyAnonamouseRedirectSecurityIntegrationTests")]
    [Trait("Category", "IndexerSearchProvider")]
    public class MyAnonamouseRedirectSecurityIntegrationTests
    {
        // AC: AC-MAM-001..010 evidence prerequisite — request observations are immutable,
        // ordered snapshots rather than retained HttpRequestMessage instances.
        // Value Score: 100
        // Behavior: Two tracked requests are sent and disposed -> the mock copies URI and header
        // values at send time -> both snapshots remain distinct, ordered, stable, and read-only.
        // @category: integration
        // @lane: integration
        // @dependency: BaseApiMock, MyAnonamouseApiMock, HttpClient
        // @real-dependency: BaseApiMock, MyAnonamouseApiMock
        // @complexity: medium
        // Verification items:
        // - Both literal request URIs and Cookie values remain available after disposal.
        // - The first Host value remains independent of later request activity.
        // - The history collection and copied header dictionary reject mutation.
        // Primary failure mode: disposed or mutable request state hides or rewrites an earlier target
        // or credential-bearing request.
        // Proof obligation: observe two independently routed requests through the HTTP test-handler
        // boundary and assert literal copied values plus read-only collection behavior.
        [Fact]
        public async Task RequestHistory_RetainsOrderedCopiedValuesAfterDisposalAndLaterRequests()
        {
            using var handler = new MyAnonamouseApiMock();
            handler.AddTrackedRoute(
                "history-first",
                new MyAnonamouseMockRoute(
                    "/history/first",
                    (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
                {
                    Method = HttpMethod.Get
                });
            handler.AddTrackedRoute(
                "history-second",
                new MyAnonamouseMockRoute(
                    "/history/second",
                    (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
                {
                    Method = HttpMethod.Get
                });
            using var client = new HttpClient(handler);

            using (var firstRequest = new HttpRequestMessage(HttpMethod.Get, "https://www.myanonamouse.net/history/first"))
            {
                firstRequest.Headers.Host = "www.myanonamouse.net";
                firstRequest.Headers.Add("Cookie", "mam_id=first_mam");
                using var firstResponse = await client.SendAsync(firstRequest);
            }

            using (var secondRequest = new HttpRequestMessage(HttpMethod.Get, "https://www.myanonamouse.net/history/second"))
            {
                secondRequest.Headers.Add("Cookie", "mam_id=second_mam");
                using var secondResponse = await client.SendAsync(secondRequest);
            }

            var history = handler.RequestHistory;

            Assert.Equal(2, history.Count);
            Assert.Equal("https://www.myanonamouse.net/history/first", history[0].RequestUri.AbsoluteUri);
            Assert.Equal(["mam_id=first_mam"], history[0].GetHeaderValues("Cookie"));
            Assert.Equal(["www.myanonamouse.net"], history[0].GetHeaderValues("Host"));
            Assert.Equal("https://www.myanonamouse.net/history/second", history[1].RequestUri.AbsoluteUri);
            Assert.Equal(["mam_id=second_mam"], history[1].GetHeaderValues("Cookie"));
            Assert.Throws<NotSupportedException>(
                () => ((IList<ApiRequestSnapshot>)history).Add(history[0]));
            Assert.Throws<NotSupportedException>(
                () => ((IDictionary<string, IReadOnlyList<string>>)history[0].Headers)
                    .Add("Authorization", ["copied-secret"]));
        }

        // AC: AC-MAM-001..010 evidence prerequisite — per-test observation reset clears
        // history and counters without removing registered routes.
        // Value Score: 73
        // Behavior: A tracked route is invoked, observations are reset, and the same route is invoked
        // again -> observation state restarts -> the route still succeeds with one new snapshot/count.
        // @category: integration
        // @lane: integration
        // @dependency: BaseApiMock, MyAnonamouseApiMock, HttpClient
        // @real-dependency: BaseApiMock, MyAnonamouseApiMock
        // @complexity: medium
        // Verification items:
        // - Reset clears request history, route count, and total call count.
        // - The previously registered route remains executable after reset.
        // - The post-reset request creates exactly one new snapshot and route invocation.
        // Primary failure mode: reset either leaks prior observations or removes route registration,
        // making later security proofs stateful or unable to exercise the same route.
        // Proof obligation: invoke the identical registered route before and after reset and assert
        // zeroed intermediate state followed by one successful, newly observed invocation.
        [Fact]
        public async Task ResetObservations_ClearsHistoryAndRouteInvocationCounts()
        {
            using var handler = new MyAnonamouseApiMock();
            handler.AddTrackedRoute(
                "reset-proof",
                new MyAnonamouseMockRoute(
                    "/history/reset",
                    (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
                {
                    Method = HttpMethod.Get
                });
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync("https://www.myanonamouse.net/history/reset");

            Assert.Single(handler.RequestHistory);
            Assert.Equal(1, handler.GetRouteInvocationCount("reset-proof"));

            handler.ResetObservations();

            Assert.Empty(handler.RequestHistory);
            Assert.Equal(0, handler.GetRouteInvocationCount("reset-proof"));
            Assert.Equal(0, handler.GetCallCount());

            using var postResetResponse = await client.GetAsync(
                "https://www.myanonamouse.net/history/reset");

            Assert.Equal(HttpStatusCode.OK, postResetResponse.StatusCode);
            Assert.Single(handler.RequestHistory);
            Assert.Equal(1, handler.GetRouteInvocationCount("reset-proof"));
        }

        // AC: AC-MAM-001..010 evidence prerequisite — unexpected outbound requests fail
        // deterministically while remaining visible in immutable history.
        // Value Score: 90
        // Behavior: Fail-on-unexpected mode receives an unregistered request -> routing records the
        // attempt and rejects it -> the caller receives the exact failure and one history snapshot.
        // @category: edge-case
        // @lane: integration
        // @dependency: BaseApiMock, MyAnonamouseApiMock, HttpClient
        // @real-dependency: BaseApiMock, MyAnonamouseApiMock
        // @complexity: low
        // Verification items:
        // - The unregistered request throws the literal deterministic exception.
        // - The unexpected attempt remains represented by exactly one history snapshot.
        // Primary failure mode: an unmatched request silently returns a canned response or disappears
        // from observations, allowing an automatic redirect or off-path send to go unnoticed.
        // Proof obligation: enable deterministic unmatched-route failure, send one literal unexpected
        // URI, and assert both the exact exception message and recorded attempt.
        [Fact]
        public async Task FailOnUnexpectedCalls_ThrowsForUnregisteredRoute()
        {
            using var handler = new MyAnonamouseApiMock
            {
                FailOnUnexpectedCalls = true
            };
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetAsync("https://www.myanonamouse.net/history/unexpected"));

            Assert.Equal(
                "Unexpected MyAnonamouse API request: GET https://www.myanonamouse.net/history/unexpected",
                exception.Message);
            Assert.Single(handler.RequestHistory);
        }

        // AC: AC-MAM-006 and AC-MAM-008 evidence prerequisite — a task-specific proof route
        // overrides the broad default download route and exposes its exact response.
        // Value Score: 90
        // Behavior: A URI matches both the tracked proof route and broad download route -> routing
        // selects the proof route -> only its counter increments and its exact poison cookie is returned.
        // @category: integration
        // @lane: integration
        // @dependency: BaseApiMock, MyAnonamouseApiMock, HttpClient
        // @real-dependency: BaseApiMock, MyAnonamouseApiMock
        // @complexity: medium
        // Verification items:
        // - The tracked proof route is invoked exactly once.
        // - The broad default download route is invoked zero times.
        // - Set-Cookie equals the single literal poison-cookie header value.
        // Primary failure mode: the broad default route shadows the proof route, so zero-invocation
        // security assertions or seventh-request poison responses do not test the intended boundary.
        // Proof obligation: register the overlapping proof route first, invoke the overlapping URI,
        // and assert exclusive route selection plus the exact canonical Set-Cookie value.
        [Fact]
        public async Task TrackedProofRoute_TakesPrecedenceOverBroadDefaultRoute()
        {
            using var handler = new MyAnonamouseApiMock();
            handler.AddTrackedRoute(
                "poison-seventh-proof",
                new MyAnonamouseMockRoute(
                    @"/tor/download\.php/security/seventh",
                    (_, _) => Task.FromResult(handler.AddCookies(
                        new HttpResponseMessage(HttpStatusCode.OK),
                        "poison_untrusted_mam")))
                {
                    Method = HttpMethod.Get
                });
            using var client = new HttpClient(handler);

            using var response = await client.GetAsync(
                "https://www.myanonamouse.net/tor/download.php/security/seventh");

            Assert.Equal(1, handler.GetRouteInvocationCount("poison-seventh-proof"));
            Assert.Equal(0, handler.GetRouteInvocationCount("download"));
            Assert.Equal(
                "mam_id=\"poison_untrusted_mam\"; Path=/; HttpOnly",
                Assert.Single(response.Headers.GetValues("Set-Cookie")));
        }
    }
}

// MyAnonamouse Redirect Security Integration Test Skeleton
// Design Doc: docs/design/myanonamouse-redirect-security-design.md
// Generated: 2026-06-21 | Budget Used: 3/3 integration, 0/3 fixture-e2e, 0/2 service-integration-e2e
//
// Artifact contract:
// - Executable content before production implementation is limited to immutable request-history
//   helper assertions; the security behavior cases below remain comments-only.
// - Implement through the public MyAnonamouseTorrentPreparationService.PrepareAsync entry point.
// - Keep internal URI/origin validation, EfIndexerRepository over EF Core InMemory, and
//   DownloadCachedTorrentStore real.
// - Mock only the MyAnonamouse HTTP endpoint through the dedicated named no-auto-redirect client.
// - Record every request as an immutable snapshot of URI and relevant header values. Assertions
//   must not depend on mutable or disposed HttpRequestMessage instances.
// - AC-MAM-009 also requires source/configuration verification. Behavioral request history proves
//   redirect visibility and parity, but does not by itself prove HttpClientHandler configuration or
//   private call-path structure.
//
// Selected coverage:
// - Test 1: AC-MAM-001, AC-MAM-002, AC-MAM-005, AC-MAM-008, AC-MAM-009, AC-MAM-010
// - Test 2: AC-MAM-003, AC-MAM-004, AC-MAM-007, AC-MAM-010, AC-MAM-011
// - Test 3: AC-MAM-006, AC-MAM-009, AC-MAM-010
// - Repository QA trace: AC-MAM-012 is verified by the exact git diff checks documented below;
//   it is not an integration-test candidate because it does not exercise runtime system behavior.
//
// Candidate selection:
// - Rejected-destination matrix: Value Score 90; selected for credential-disclosure prevention.
// - Trusted same-origin lifecycle: Value Score 100; selected for core compatibility and persistence.
// - Redirect-limit parity: Value Score 54; selected for bounded primary/retry behavior.
// - Lower-value duplicates are pushed down to existing focused rewrite, announce, extraction, and
//   compatibility suites named by the Design Doc.
//
// Test case: Reject every untrusted configured, initial, or redirected target before transmission
//
// AC: "AC-MAM-001 — When the configured indexer URL is not an absolute HTTPS URI, contains embedded credentials, or resolves to a private/loopback address, the system shall stop preparation before sending any torrent request."
// AC: "AC-MAM-002 — When the initial torrent URI differs from the configured indexer origin by scheme, normalized host, or effective port, the system shall reject it before sending a request and shall not include mam_id in any observed request."
// AC: "AC-MAM-005 — When a redirect resolves to another host, HTTP, another effective port, embedded credentials, a private/loopback target, or another invalid target, the system shall reject it before sending the redirected request."
// AC: "AC-MAM-008 — When a response is not attributable to a validated trusted-origin request, the system shall not accept, use, or persist a mam_id value from that response."
// AC: "AC-MAM-009 — While the primary path is active, the system shall use the shared validated send path and a client with automatic redirects disabled."
// AC: "AC-MAM-010 — When a non-torrent primary response triggers the authenticated retry, the retry shall enforce the same target validation, exact-origin credential rule, cookie update rule, and redirect limit as the primary path."
// Value Score: 90 | Business Value: 10 | User Frequency: 8 | Legal Requirement: false | Defect Detection: 10
// Behavior: Invalid configured origin, initial target, or primary/retry redirect -> validate before request construction and sending -> reject without credential disclosure, cookie mutation, or torrent caching
// @category: core-functionality
// @lane: integration
// @dependency: MyAnonamouseTorrentPreparationService, dedicated named HttpClient, MyAnonamouseApiMock, IIndexerRepository
// @real-dependency: MyAnonamouseTorrentPreparationService, OutboundRequestSecurity, EfIndexerRepository, EF Core InMemory
// @complexity: high
// Primary failure mode: a rejected URI receives a request, any observed off-origin request contains mam_id, or an untrusted response changes persisted credentials
// Proof obligation: use a data matrix covering malformed configured-indexer text, a relative
// configured-indexer URL, HTTP, embedded credentials, and private/loopback configured origins;
// scheme, normalized-host, and effective-port mismatches on the initial URI; and absolute/relative
// redirects resolving to cross-host, HTTP, alternate-port, credentialed, private/loopback, malformed,
// or unsupported targets. Every configured-indexer matrix row, including malformed and relative
// values, must assert zero immutable request-history snapshots, repository reload still containing
// exactly mam_id=old_mam, SearchResult.TorrentFileContent remaining null, and no cached torrent bytes
// or announces for the download ID. Run redirect rejection through both the primary path and the
// non-torrent-triggered authenticated retry. For each initial/redirect row, assert the immutable
// ordered request history contains only requests whose URI has the exact configured HTTPS scheme,
// normalized IdnHost, and effective port. The rejected target must be absent from history, no
// observed off-origin snapshot may contain Cookie, persisted mam_id must remain the original literal
// value, and SearchResult.TorrentFileContent and cache state must remain unset. Mock only endpoint
// responses; keep validation, repository, and cache paths real.
//
// AC-MAM-008 poison-cookie setup: register the rejected/untrusted route so that, if incorrectly
// invoked, it increments a dedicated invocation count and returns Set-Cookie:
// mam_id=poison_untrusted_mam. Assert that route's invocation count remains exactly 0, no request
// snapshot targets it, no later trusted request carries mam_id=poison_untrusted_mam, and repository
// reload still contains mam_id=old_mam. This negative proof applies only to a route that was never
// validated or sent; it must not reject or invalidate Set-Cookie updates returned by an actually
// sent, validated trusted-origin redirect response.
//
// AC-MAM-009 structural proof obligation: inspect the dedicated named-client registration and assert
// in source/configuration review that its primary handler is HttpClientHandler with
// AllowAutoRedirect=false. Inspect MyAnonamouseTorrentPreparationService and assert both the primary
// flow and authenticated retry invoke the same private SendValidatedAsync(ValidatedSendRequest)
// operation, with no alternate SendAsync loop or concrete authenticated client bypass. Behavioral
// history must additionally show redirect responses are application-visible, but history alone is
// not accepted as proof of handler configuration or shared private call-path structure.
// Verification items:
// - Malformed and relative configured-indexer URLs each produce zero request-history snapshots,
//   preserve repository mam_id=old_mam, leave SearchResult content null, and create no cache content.
// - Other invalid configured origins produce the same zero-send, unchanged-repository, no-cache result.
// - Invalid initial targets produce zero request-history snapshots and no credential-bearing request.
// - Invalid redirected targets are absent; only earlier trusted-origin requests appear in history.
// - Primary and retry matrices have identical destination, credential, persistence, and cache results.
// - The poison route would return mam_id=poison_untrusted_mam if invoked, is invoked zero times, and
//   its poison value is never observed in request history or persisted repository state.
// - The dedicated named client source/configuration sets HttpClientHandler.AllowAutoRedirect=false.
// - Primary and retry source paths both call SendValidatedAsync with no alternate sending bypass.
// - Request history alone is not accepted as proof of handler configuration or shared-path structure.
// - Request-history entries retain copied URI and header values after request disposal or later activity.
// Expected results:
// - Every transmitted URI equals the trusted origin by HTTPS scheme, normalized IdnHost, and effective port.
// - Repository reload returns the original mam_id and no rejected flow caches torrent content.
// Pass criteria:
// - All rejection matrix rows satisfy zero pre-send leakage and primary/retry parity with literal assertions.
//
// Test case: Preserve trusted direct, relative-redirect, and absolute-redirect torrent flows
//
// AC: "AC-MAM-003 — When the initial torrent URI matches the exact configured HTTPS origin and passes URI and DNS validation, the system shall send the request with the current mam_id."
// AC: "AC-MAM-004 — When a response redirects to an absolute or relative URI on the same trusted origin, the system shall validate the resolved URI and follow it while reapplying the current mam_id."
// AC: "AC-MAM-007 — When a response to a validated trusted-origin request contains Set-Cookie: mam_id=<new-value>, the system shall use and persist the new value before a subsequent same-origin redirect request."
// AC: "AC-MAM-010 — When a non-torrent primary response triggers the authenticated retry, the retry shall enforce the same target validation, exact-origin credential rule, cookie update rule, and redirect limit as the primary path."
// AC: "AC-MAM-011 — When a legitimate same-origin flow returns valid torrent bytes, the system shall continue to set SearchResult.TorrentFileContent, preserve the resolved filename, and expose cached torrent content and announces through the existing cache accessors."
// Value Score: 100 | Business Value: 10 | User Frequency: 9 | Legal Requirement: false | Defect Detection: 10
// Behavior: Valid direct or same-origin redirected response -> send only to validated targets and apply trusted cookie refresh -> preserve torrent result, filename, repository state, cached bytes, and announces
// @category: core-functionality
// @lane: integration
// @dependency: MyAnonamouseTorrentPreparationService, dedicated named HttpClient, MyAnonamouseApiMock, IIndexerRepository, DownloadCachedTorrentStore
// @real-dependency: MyAnonamouseTorrentPreparationService, OutboundRequestSecurity, EfIndexerRepository, EF Core InMemory, DownloadCachedTorrentStore
// @complexity: high
// Primary failure mode: valid same-origin traffic is rejected, a redirect loses or mis-scopes mam_id, the trusted update is not persisted before the next request, or successful torrent/cache outputs change
// Proof obligation: cover a direct request plus relative and absolute redirects that resolve to the
// exact configured HTTPS origin, including implicit https port and explicit port 443 equivalence.
// Return Set-Cookie: mam_id=trusted_refresh from the first trusted redirect response. In the mock
// handler callback for the subsequent trusted redirect request, before constructing or returning
// that request's response, reload the indexer through the real repository and assert its stored
// AdditionalSettings already contains exactly mam_id=trusted_refresh; also assert the current request
// snapshot carries exactly Cookie: mam_id=trusted_refresh. This handler-time assertion is the temporal
// proof that persistence completed before the subsequent trusted request was handled, rather than
// merely by the end of PrepareAsync. Complete with valid torrent bytes and assert before/action/after
// state: content starts null, trusted requests occur in order, then SearchResult content and filename
// plus cache bytes and announces match the established literals. Repeat the valid redirect contract
// through authenticated retry where needed to prove the shared rules without duplicating lower-level
// helper tests.
// Verification items:
// - Direct trusted request snapshot contains Cookie: mam_id=old_mam.
// - Relative and absolute same-origin redirects resolve to the expected literal trusted URIs.
// - Redirect response is visible to the handler and the following request uses trusted_refresh.
// - During handling of the subsequent trusted request, repository reload already contains trusted_refresh
//   before that handler returns its response.
// - SearchResult.TorrentFileContent is non-empty and TorrentFileName equals the resolved literal filename.
// - Cached response remains application/x-bittorrent with the expected filename and byte array.
// - Cached announces remain available and contain the expected normalized mam_id augmentation.
// - Immutable history preserves each request's original URI and Cookie value independently.
// Expected results:
// - All requests stay on the exact trusted HTTPS origin and successful output matches existing cache contracts.
// Pass criteria:
// - Direct, relative, absolute, and retry variants preserve literal credential, persistence, torrent, filename, cache, and announce expectations.
//
// Test case: Enforce one initial request plus five redirects independently on primary and retry paths
//
// AC: "AC-MAM-006 — When a request chain contains more than five redirects, the system shall stop after no more than six total requests for that path and shall not cache torrent content from a seventh request."
// AC: "AC-MAM-009 — While the primary path is active, the system shall use the shared validated send path and a client with automatic redirects disabled."
// AC: "AC-MAM-010 — When a non-torrent primary response triggers the authenticated retry, the retry shall enforce the same target validation, exact-origin credential rule, cookie update rule, and redirect limit as the primary path."
// Value Score: 54 | Business Value: 9 | User Frequency: 5 | Legal Requirement: false | Defect Detection: 9
// Behavior: Same-origin chain offers a sixth redirect and seventh response -> count each manually observed request -> stop each primary/retry path at six requests without accepting seventh-request torrent content
// @category: edge-case
// @lane: integration
// @dependency: MyAnonamouseTorrentPreparationService, dedicated named HttpClient, MyAnonamouseApiMock, DownloadCachedTorrentStore
// @real-dependency: MyAnonamouseTorrentPreparationService, OutboundRequestSecurity, DownloadCachedTorrentStore
// @complexity: high
// Primary failure mode: either path sends a seventh request, path counts diverge, or torrent bytes available only from the seventh response enter SearchResult or cache
// Proof obligation: configure deterministic same-origin redirect chains whose first six segment
// responses are observable redirects and whose seventh segment route alone returns valid torrent
// bytes. Exercise the primary path and authenticated retry as separate variants with isolated state.
// For the primary-limit variant, assert the primary segment contains exactly six immutable snapshots
// and no seventh URI. For the retry-limit variant, first record exactly one separately identified
// primary trigger request that returns the non-torrent payload activating retry; then mark the
// beginning of the retry segment and assert that segment alone contains exactly six immutable
// snapshots, sequential trusted URIs, current Cookie values, and no seventh retry URI. The retry
// variant's total history is therefore exactly seven requests: one primary non-torrent trigger plus
// six retry-segment requests. Assert SearchResult.TorrentFileContent remains null and cache accessors
// expose no seventh-segment bytes or announces. The handler must fail on unexpected calls so an
// automatic redirect or off-by-one send cannot remain invisible. Apply the AC-MAM-009 structural
// source/configuration proof from Test 1 here as a prerequisite; request counts prove behavior, not
// AllowAutoRedirect configuration or private call-path identity.
// Verification items:
// - Primary path records exactly six trusted-origin requests and never invokes the seventh route.
// - Retry variant records one separately identified primary non-torrent trigger request before the retry segment.
// - The retry segment alone contains exactly six trusted-origin requests and never invokes its seventh route.
// - Retry variant total request history equals 7: 1 primary trigger + 6 retry-segment requests.
// - Each request is represented by a distinct immutable URI/header snapshot in execution order.
// - No torrent content, filename, cached bytes, or announces originate from the seventh route.
// Expected results:
// - The primary segment and retry segment each stop at the identical six-request boundary and leave result/cache state unchanged.
// Pass criteria:
// - Primary segment count equals 6; retry history equals one trigger plus a retry-segment count of 6;
//   both seventh segment routes remain uninvoked and all seventh-response output remains absent.
//
// Repository QA obligation: AC-MAM-012
//
// AC: "AC-MAM-012 — When git diff --check origin/canary...HEAD is run after implementation, it shall report none of the eight approved trailing-whitespace diagnostics and no additional formatting-only changes shall be present."
// Classification: [UNIT_LEVEL] Repository diff verification; excluded from the integration-test budget.
// Proof obligation: after implementation, run git diff --check origin/canary...HEAD and inspect the
// formatting-only diff. The command must report no whitespace errors. Formatting-only production
// edits must be limited to the eight exact file/line diagnostics in the Design Doc's Authoritative
// Whitespace Diagnostic Inventory; this skeleton file is the only generation-time repository edit.
// Expected result: no trailing-whitespace diagnostics and no unrelated formatting cleanup.
// Pass criteria: clean diff check plus manual path/line comparison against all eight approved entries.
//
// Generation report:
// {"status":"completed","feature":"myanonamouse-redirect-security","generatedFiles":{"integration":"tests/Features/Api/Services/Search/Providers/MyAnonamouseRedirectSecurityIntegrationTests.cs","fixtureE2e":null,"serviceE2e":null},"budgetUsage":{"integration":"3/3","fixtureE2e":"0/3","serviceE2e":"0/2"},"e2eAbsenceReason":{"fixtureE2e":"no_user_facing_multi_step_journey","serviceE2e":"no_real_service_dependency"},"boundaryProofGaps":[]}
