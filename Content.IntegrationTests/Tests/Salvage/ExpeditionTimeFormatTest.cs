using Content.Client.Salvage.UI;

namespace Content.IntegrationTests.Tests.Salvage;

/// <summary>
/// The expedition console's countdowns showed only the minutes part of the time left, so anything over an hour wrapped
/// around (a 2 hour expedition showed 00:00).
/// </summary>
[TestFixture]
public sealed class ExpeditionTimeFormatTest
{
    [Test]
    [TestCase(0, 45, "00:45")]
    [TestCase(9, 5, "09:05")]
    [TestCase(59, 59, "59:59")]
    [TestCase(60, 0, "1:00:00")]
    [TestCase(120, 0, "2:00:00")]
    [TestCase(135, 7, "2:15:07")]
    public void ConsoleCountdownShowsHours(int minutes, int seconds, string expected)
    {
        var remaining = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        Assert.That(OfferingWindow.FormatRemaining(remaining), Is.EqualTo(expected));
    }
}
