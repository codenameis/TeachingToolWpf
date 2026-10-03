using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using TeachingToolWpf.Controls;

namespace TeachingToolWpf.Imaging.Tests;

public class ValueTextBoxTests
{
    [Fact]
    public void InactiveUpdatesPreserveValueAccessibilityWithoutEditingTheTextStore()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var field = new ValueTextBox();
                var changes = 0;
                field.TextChanged += (_, _) => changes++;
                for (var i = 0;
i < 1000;
i++)
                {
                    field.Value = $"영역 {i}";
                }

                Assert.Equal(0, changes);
                var peer = UIElementAutomationPeer.CreatePeerForElement(field);
                var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
                Assert.Equal("영역 999", value.Value);
                value.SetValue("한글 수정");
                Assert.Equal("한글 수정", field.Value);
                field.IsReadOnly = true;
                Assert.Throws<InvalidOperationException>(() => value.SetValue("금지"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
