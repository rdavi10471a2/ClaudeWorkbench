# Workflow Harness Sample

This is a small committed watched solution for AIMonitor workflow smokes.

Use it when a test needs a stable, disposable watched project rather than a
developer's real application. The sample intentionally includes an
`AppConfig/AppConfig.cs` file under `WorkflowHarnessSample.Configuration` so
new-file, member-pair, syntax-rejection, and pre-merge validation workflows can
exercise the same protected Working/stage/WinMerge/decision flow.
