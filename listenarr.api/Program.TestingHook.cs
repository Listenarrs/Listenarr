public partial class Program
{
    // Declarations for test host patches used by the integration test host partial
    // Implementation is supplied in Program.Testing.cs (only compiled/used by test host runs).
    internal static partial void ApplyTestHostPatches(WebApplicationBuilder builder);
}
