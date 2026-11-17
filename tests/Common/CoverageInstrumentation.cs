using System.Collections.Concurrent;
using System.Reflection;

namespace Listenarr.Tests.Common
{
    /// <summary>
    /// Wall-clock guardrails for code that coverage collection may have instrumented. A CI run with the coverage input (or a
    /// local run with tests/coverage.runsettings) collects backend coverage with coverlet (tests/coverage.runsettings), which adds a hit counter to every line of
    /// Listenarr's assemblies and makes hot loops several times slower; a limit meant for the real code
    /// would fail on the instrumented one.
    /// </summary>
    public static class CoverageInstrumentation
    {
        /// <summary>How much slower instrumented code may be before a guardrail fails.</summary>
        public const int InstrumentedSlowdownAllowance = 3;

        private static readonly ConcurrentDictionary<Assembly, bool> Instrumented = new();

        /// <summary>Whether coverlet instrumented the assembly: it adds its tracker types to it.</summary>
        public static bool IsInstrumented(Assembly assembly) =>
            Instrumented.GetOrAdd(assembly, static candidate =>
            {
                try
                {
                    return candidate.GetTypes().Any(type =>
                        type.Namespace?.StartsWith("Coverlet.Core.Instrumentation.Tracker", StringComparison.Ordinal) == true);
                }
                catch (ReflectionTypeLoadException exception)
                {
                    return exception.Types.Any(type =>
                        type?.Namespace?.StartsWith("Coverlet.Core.Instrumentation.Tracker", StringComparison.Ordinal) == true);
                }
            });

        /// <summary>
        /// The time limit for code of <paramref name="codeUnderTest"/>'s assembly: <paramref name="milliseconds"/>,
        /// or <see cref="InstrumentedSlowdownAllowance"/> times that when coverage instrumented it.
        /// </summary>
        public static double TimeLimitMilliseconds(Type codeUnderTest, double milliseconds) =>
            IsInstrumented(codeUnderTest.Assembly) ? milliseconds * InstrumentedSlowdownAllowance : milliseconds;
    }
}
