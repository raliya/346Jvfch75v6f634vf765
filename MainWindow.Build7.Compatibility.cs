namespace TextFileProcessor;

public partial class MainWindow
{
    private void CaptureBuild7Error(params object[] arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument is System.Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                return;
            }
        }
    }

    private void CompleteBuild7LegacyOperation(params object[] arguments)
    {
        // Совместимость со старым кодом Build7.
    }
}