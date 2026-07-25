using Namager.App.ViewModels;
using Sonulab.Core.Transport;
using Xunit;

public class LinkProviderWiringTests
{
    // The app is USB-only by decision (2026-07-25 spec). This is a regression guard: the WiFi
    // transport still exists and still compiles, so re-adding it to the provider list is a
    // two-line accident. Asserting only on the disconnected-status string would NOT catch that.
    [Fact] public void App_offers_exactly_one_transport_and_it_is_USB()
    {
        var providers = MainWindowViewModel.BuildProviders(new SerialLinkOptions());

        Assert.Single(providers);
        Assert.Equal("USB", providers[0].Name);
    }
}
