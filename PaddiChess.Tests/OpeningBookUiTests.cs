using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class OpeningBookUiTests
{
    [Fact]
    public async Task UserLibrarySelectionPersistsAndOnlyAffectsPlayingSearches()
    {
        await ExternalSessionTests.Session.Dispatch(async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "paddi-book-ui-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path.Combine(folder, "settings.json"));
            MainWindow? window = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var store = new OpeningBookStore(folder);
                var profile = await store.ImportAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpeningBooks", "sample.xqb"));
                new AppPreferences { AutoAnalyze = false, OpeningBooks = [profile], OpeningBookId = profile.Id }.Save();
                window = new MainWindow(); window.Show();
                Assert.Equal(profile.Id, ((OpeningBookProfile)window.FindControl<ComboBox>("BookChoiceBox")!.SelectedItem!).Id);
                window.FindControl<NumericUpDown>("BookRoundsBox")!.Value = 12;
                window.FindControl<ComboBox>("BookPolicyBox")!.SelectedIndex = 1;
                EngineSettings Settings(string method) => (EngineSettings)typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, null)!;
                var playing = Settings("ReadPlayingEngineSettings");
                Assert.Equal(store.Resolve(profile), playing.OpeningBook!.Path);
                Assert.Equal(12, playing.OpeningBook.MaxFullmoves);
                Assert.True(playing.OpeningBook.RandomBest);
                Assert.Null(Settings("ReadEngineSettings").OpeningBook);
                window.FindControl<CheckBox>("BookEnabledCheck")!.IsChecked = false;
                Assert.Null(Settings("ReadPlayingEngineSettings").OpeningBook);
                window.FindControl<Button>("SettingsWorkspaceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var capture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui/opening-book-settings.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
                using (var frame = window.CaptureRenderedFrame()) frame?.Save(capture);
                window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", flags)!.GetValue(window)!; window = null;
                var saved = AppPreferences.Load();
                Assert.False(saved.OpeningBookEnabled); Assert.Equal(profile.Id, saved.OpeningBookId);
                Assert.Equal(12, saved.OpeningBookMaxFullmoves); Assert.True(saved.OpeningBookRandomBest);
                Assert.Single(saved.OpeningBooks);
            }
            finally
            {
                if (window != null) { window.Close(); await (Task)typeof(MainWindow).GetField("_cleanupTask", flags)!.GetValue(window)!; }
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previous);
                Directory.Delete(folder, true);
            }
            return true;
        }, default);
    }
}
