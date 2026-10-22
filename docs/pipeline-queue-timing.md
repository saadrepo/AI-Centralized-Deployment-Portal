# Queue latency diagnostics

Code inspection found a duplicated definition lookup (stored read identity followed by visiting user) and sequential independent branch/selected-build validation. Queue now reads the definition once as the verified Windows caller and runs the two validations concurrently. All validation completes before the single queue POST. No mutation retries, permission caches or service-account write fallback were introduced. Approval code is unchanged.

The queue action returns as soon as TFS supplies its build ID; it does not wait for an agent. Existing immediate spinner, duplicate-click lock and real-ID status polling remain in place.

Diagnostics (no credentials, tokens, request bodies or upstream error bodies):

- Browser DevTools console `Pipeline timing`: Queue/History/Status HTTP headers and total response milliseconds, HTTP status and Server-Timing. Browser time includes local Windows authentication/network wait; it is not an isolated authentication measurement.
- Response `Server-Timing: pipeline;dur=...`: controller service execution milliseconds, excluding pre-controller browser/ASP.NET authentication and response serialization.
- Server logs `CI queue timing`: WindowsIdentity (transport setup plus connectionData and verified identity), Definition, Branches, SelectedBuild, QueuePostHeaders, QueueResponseBody and Total. Queue operations have a logging scope for correlation; controller logs include request references. Timed API phases log elapsed duration on failure too; Total identifies the failing stage if identity verification fails.
- Server logs `CI agent queue timing`: actual build queueTime-to-startTime elapsed; when startTime is absent, elapsed wait so far is explicitly marked Started=false. These are read during status/history polling, never by waiting inside the queue action.

Compare browser duration with Server-Timing, then the individual server phases. A long WindowsIdentity phase points to outgoing Windows/TFS authentication or connectionData; a long QueuePostHeaders phase points to TFS acceptance/transport, while a long agent wait after the ID is a separate scheduling delay. Browser-only excess requires the DevTools Network authentication/connection waterfall to distinguish the causes.

No live queue request was executed, so the reported approximately 20-second delay has not been measured or attributed conclusively. The code bottlenecks above are confirmed by inspection; actual environment attribution requires the next user-authorized queue request.

Changed files: PipelineService.cs, PipelineController.cs, pipeline.js, PipelineTests.cs, pipeline-client.test.cjs and this report. Dashboard, approval rules, production settings, credentials, database, IIS and release scripts were untouched.
