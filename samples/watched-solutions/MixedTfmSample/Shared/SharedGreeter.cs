namespace Shared;

/// <summary>
/// The single cross-project symbol every app in MixedTfmSample calls. It lives at the lowest
/// TFM (net8.0) so the console (net8), WinForms (net9) and Blazor (net10) projects can all
/// reference it. Change this method's signature and update a consumer to reproduce an
/// interdependent cross-project edit across the mixed target frameworks.
/// </summary>
public static class SharedGreeter
{
    public static string Greet(string name) => $"Hello, {name}, from Shared (net8.0).";
}
