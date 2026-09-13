using System.Windows;
using TextFileProcessor.Models;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow
{
    private readonly DatabaseDeploymentService
        _databaseDeploymentService = new();

    private void Build4_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Build3_Loaded(sender, e);

        Build4StatusTextBlock.Text =
            "Выберите готовое задание, затем создайте БД " +
            "и импортируйте SQL.";
    }

    private async void DeployDatabaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RunSelectedJobsFixAsync("db");
    }
}