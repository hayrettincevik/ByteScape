using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ByteScape.Core.Games;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// "Which games take my space, and which ones do I still play?": installed games with their folders (game files,
/// shader cache, workshop content, saves), when they were last played, and what uninstalled games left behind.
/// </summary>
public partial class GamesWindow : Window
{
    private readonly ScanView _view;
    private GameAuditResult? _result;

    internal sealed class GameRow
    {
        public required GameUsage? Usage { get; init; }
        public required IReadOnlyList<GamePiece> Pieces { get; init; }
        public required string Name { get; init; }
        public string Store { get; init; } = "";
        public long Size { get; init; }
        public long Game { get; init; }
        public long Extra { get; init; }
        public bool IsIdle { get; init; }
        public bool IsLeftovers => Usage == null;
        public string SizeText => Format.Size(Size) + (Usage is { InView: false } ? " *" : "");
        public string GameText => Usage is { InView: true } ? Format.Size(Game) : "";
        public string ExtraText => Usage != null && Extra > 0 ? Format.Size(Extra) : "";
        public string PlayedText => Usage == null ? "" : Usage.Game.LastPlayed is { } t ? AppsWindow.Ago(t) : Usage.Game.Store == GameStore.Steam ? T("never") : "—";
        public string PlaytimeText => Usage?.Game.Playtime is { } p ? GameAudit.PlaytimeText(p) : "";
        public string UpdatedText => Usage?.Game.LastUpdated is { } u ? AppsWindow.Ago(u) : "";
    }

    internal sealed record PieceRow(GamePiece Piece)
    {
        public string What => Piece.Game != null ? Piece.KindText : Piece.Kind == GamePieceKind.Leftover ? T("Game folder") : Piece.KindText;
        public string SizeText => Format.Size(Piece.Bytes);
        public string Note => Piece.Note;
        public string Path => Piece.Path;
    }

    public GamesWindow(ScanView view)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
        Closed += (_, _) => { _view.Model.Spotlight = null; _view.RefreshMap(); };
    }

    private TimeSpan IdleSpan => TimeSpan.FromDays(double.Parse((string)((ComboBoxItem)Idle.SelectedItem).Tag));

    public void SetResult(GameAuditResult result, bool scanRunning)
    {
        var selected = (GameList.SelectedItem as GameRow)?.Usage?.Game;
        _result = result;
        var now = DateTime.UtcNow;
        var notes = new List<string>();
        if (scanRunning) notes.Add(T("The scan is still running: sizes grow until it completes (press Refresh then)."));
        int outside = result.Games.Count(g => !g.InView);
        if (outside > 0) notes.Add(P(outside, "{0} game is outside this view: its size (*) is what the launcher reports.", "{0} games are outside this view: their size (*) is what the launcher reports."));
        PartialLine.Text = string.Join("  ", notes);
        PartialLine.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var games = result.Games.Where(g => !g.Game.IsTool).ToList();
        HeadLine.Text = P(games.Count, "{1} in {0} game", "{1} in {0} games", Format.Size(games.Sum(g => g.ShownBytes)));
        SubLine.Text = string.Join(" · ", games.GroupBy(g => g.Game.StoreText).Select(g => g.Key + " " + g.Count()))
                       + (games.Count == 0 ? T("No Steam, Epic Games or GOG games found.") : ". " + T("Sizes include shader caches, workshop content and saves."));
        var idle = result.NotPlayedFor(TimeSpan.FromDays(365), now).ToList();
        IdleText.Text = idle.Count + " · " + Format.Size(idle.Sum(g => g.ShownBytes));
        NeverText.Text = games.Count(g => g.Game.Store == GameStore.Steam && g.Game.LastPlayed == null).ToString();
        LeftText.Text = result.Leftovers.Count + " · " + Format.Size(result.LeftoverBytes);
        Fill();
        if (selected != null) Select(selected);
    }

    public void Select(GameInfo game)
    {
        var row = GameList.Items.OfType<GameRow>().FirstOrDefault(r => ReferenceEquals(r.Usage?.Game, game) || (r.Usage != null && r.Usage.Game.Store == game.Store && r.Usage.Game.Id == game.Id));
        if (row == null && game.IsTool) { ShowTools.IsChecked = true; Fill(); row = GameList.Items.OfType<GameRow>().FirstOrDefault(r => r.Usage?.Game.Id == game.Id); }
        if (row == null) { Idle.SelectedIndex = 0; Fill(); row = GameList.Items.OfType<GameRow>().FirstOrDefault(r => r.Usage?.Game.Id == game.Id); }
        if (row == null) return;
        GameList.SelectedItem = row;
        GameList.ScrollIntoView(row);
    }

    private void Fill()
    {
        var r = _result;
        if (r == null) return;
        var q = Search.Text.Trim();
        bool Hit(string s) => q.Length == 0 || s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var now = DateTime.UtcNow;
        var span = IdleSpan;
        var idle = new HashSet<GameUsage>(r.NotPlayedFor(TimeSpan.FromDays(365), now));
        var list = r.Games
            .Where(u => ShowTools.IsChecked == true || !u.Game.IsTool)
            .Where(u => span == TimeSpan.Zero || (!u.Game.IsTool && u.Game.Store == GameStore.Steam && (u.Game.LastPlayed == null || now - u.Game.LastPlayed.Value > span)))
            .Where(u => Hit(u.Game.Name) || Hit(u.Game.StoreText) || u.Pieces.Any(p => Hit(p.Path)) || Hit(u.Game.InstallDir))
            .Select(u => new GameRow
            {
                Usage = u, Pieces = u.Pieces, Name = u.Game.Name + (u.Game.IsTool ? " (" + T("tool") + ")" : ""), Store = u.Game.StoreText,
                Size = u.ShownBytes, Game = u.GameBytes, Extra = u.ExtraBytes, IsIdle = idle.Contains(u),
            })
            .ToList();
        var left = r.Leftovers.Where(p => Hit(p.Path) || Hit(p.Note)).ToList();
        if (left.Count > 0 && span == TimeSpan.Zero)
            list.Add(new GameRow { Usage = null, Pieces = left, Name = T("Left behind by uninstalled games"), Size = left.Sum(p => p.Bytes) });
        GameList.ItemsSource = list;
        if (GameList.SelectedItem == null) ShowPieces(null);
    }

    private void ShowPieces(GameRow? row)
    {
        UninstallBtn.IsEnabled = row?.Usage is { Game.Store: GameStore.Steam };
        StoreBtn.IsEnabled = SteamAppOf(row, null) != null;
        if (row == null)
        {
            PieceList.ItemsSource = null;
            PieceHead.Text = T("Select a game to see its folders.");
            _view.Model.Spotlight = null;
            _view.RefreshMap();
            return;
        }
        PieceList.ItemsSource = row.Pieces.Select(p => new PieceRow(p)).ToList();
        if (row.Usage is { } u)
        {
            var g = u.Game;
            PieceHead.Text = g.Name + " (" + g.StoreText + ") — " + Format.Size(u.ShownBytes)
                             + (g.Playtime is { } pt ? ", " + F($"played {GameAudit.PlaytimeText(pt)}") : "")
                             + (g.LastPlayed is { } lp ? ", " + F($"last on {Format.Date(lp)}") : g.Store == GameStore.Steam ? ", " + T("never started") : "")
                             + (u.InView ? "" : ". " + T("Not in this view: size reported by the launcher."));
        }
        else PieceHead.Text = P(row.Pieces.Count, "{0} folder left behind by games that are no longer installed. Saves and mods may be in there: check before deleting.", "{0} folders left behind by games that are no longer installed. Saves and mods may be in there: check before deleting.");
        UpdateSpotlight(row);
    }

    private void UpdateSpotlight(GameRow? row)
    {
        _view.Model.Spotlight = Spot.IsChecked == true && row != null && _result != null && row.Pieces.Any(p => p.Node != null) ? _result.SpotlightFor(row.Pieces) : null;
        _view.RefreshMap();
    }

    /// <summary>The Steam app id of the selected game, or of a selected leftover whose folder is named by it.</summary>
    private static string? SteamAppOf(GameRow? row, PieceRow? piece)
    {
        if (piece != null && piece.Piece.Game == null && System.IO.Path.GetFileName(piece.Piece.Path) is { } n && n.Length > 0 && n.All(char.IsAsciiDigit)) return n;
        return row?.Usage is { Game.Store: GameStore.Steam } u && u.Game.Id.Length > 0 && u.Game.Id.All(char.IsAsciiDigit) ? u.Game.Id : null;
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPieces(GameList.SelectedItem as GameRow);
    private void PieceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StoreBtn.IsEnabled = SteamAppOf(GameList.SelectedItem as GameRow, PieceList.SelectedItem as PieceRow) != null;
        ExplorerBtn.IsEnabled = _view.Tree.IsLive;
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Fill(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Fill(); }
    private void Spot_Click(object sender, RoutedEventArgs e) => UpdateSpotlight(GameList.SelectedItem as GameRow);
    private void Refresh_Click(object sender, RoutedEventArgs e) => _view.ShowGames((GameList.SelectedItem as GameRow)?.Usage?.Game);
    private void PieceList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap(fromPiece: true);
    private void GameList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap(fromPiece: false);
    private void ShowOnMap_Click(object sender, RoutedEventArgs e) => ShowOnMap(fromPiece: PieceList.SelectedItem != null);

    private void ShowOnMap(bool fromPiece)
    {
        if (fromPiece && PieceList.SelectedItem is PieceRow { Piece.Node: { } node }) _view.ShowNode(node);
        else if (GameList.SelectedItem is GameRow row && row.Pieces.Where(p => p.Node != null).Select(p => p.Node!).ToList() is { Count: > 0 } nodes)
        {
            if (nodes.Count == 1) _view.ShowNode(nodes[0]);
            else if (AppsWindow.CommonAncestor(nodes) is { } common) _view.ZoomTo(common);
        }
        else return;
        Owner?.Activate();
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        var node = (PieceList.SelectedItem as PieceRow)?.Piece.Node ?? (GameList.SelectedItem as GameRow)?.Pieces.FirstOrDefault(p => p.Node != null)?.Node;
        if (node != null && _view.Tree.IsLive) _view.OpenInExplorer(node);
    }

    private void StorePage_Click(object sender, RoutedEventArgs e)
    {
        if (SteamAppOf(GameList.SelectedItem as GameRow, PieceList.SelectedItem as PieceRow) is { } id) MainWindow.OpenUrl("https://store.steampowered.com/app/" + id);
    }

    /// <summary>Hands the game to Steam's own uninstall (Steam asks before removing anything).</summary>
    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameRow { Usage.Game: { Store: GameStore.Steam } g } && g.Id.Length > 0 && g.Id.All(char.IsAsciiDigit)) MainWindow.OpenUrl("steam://uninstall/" + g.Id);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try { Dialogs.Copy(GameAudit.ToText(_result, DateTime.UtcNow)); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog { Filter = T("Text file (*.txt)|*.txt|Tab separated (*.tsv)|*.tsv"), FileName = F($"{Branding.Name} games.txt") };
        if (Dialogs.Pick(dlg, this) != true) return;
        try { File.WriteAllText(dlg.FileName, GameAudit.ToText(_result, DateTime.UtcNow), new UTF8Encoding(false)); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
