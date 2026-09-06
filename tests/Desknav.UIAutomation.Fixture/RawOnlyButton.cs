using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Desknav.UIAutomation.Fixture;

internal sealed class RawOnlyButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RawOnlyButtonAutomationPeer(this);

    private sealed class RawOnlyButtonAutomationPeer(
        RawOnlyButton owner)
        : ButtonAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
    }
}
