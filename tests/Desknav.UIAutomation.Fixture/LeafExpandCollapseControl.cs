using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Desknav.UIAutomation.Fixture;

internal sealed class LeafExpandCollapseControl : ContentControl
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new LeafExpandCollapseAutomationPeer(this);

    private sealed class LeafExpandCollapseAutomationPeer(
        LeafExpandCollapseControl owner)
        : FrameworkElementAutomationPeer(owner),
            IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState =>
            ExpandCollapseState.LeafNode;

        public void Collapse() =>
            throw new InvalidOperationException(
                "A leaf node cannot collapse.");

        public void Expand() =>
            throw new InvalidOperationException(
                "A leaf node cannot expand.");

        public override object? GetPattern(
            PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse
                ? this
                : base.GetPattern(patternInterface);

        protected override AutomationControlType
            GetAutomationControlTypeCore() =>
            AutomationControlType.TreeItem;

        protected override string GetClassNameCore() =>
            nameof(LeafExpandCollapseControl);

        protected override string GetNameCore() =>
            owner.Content?.ToString() ?? string.Empty;
    }
}
