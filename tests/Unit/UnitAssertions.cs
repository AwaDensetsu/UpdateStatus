using NUnit.Framework;

namespace UpdateStatus.Tests.Unit
{
    internal static class UnitAssertions
    {
        internal static void Eq<T>(T expected, T actual)
        {
            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
