using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReludualiConsole
{
    /// <summary>
    /// The HELP page: a short how-to for each tool (Mycelium, 2026-10-01: "condensed tutorials"; overgrow night 2026-10-10, for the
    /// people testing on the 18th). Plain words; what each tool does, how to use it, and what it cannot do yet. 2026-10-14: the
    /// rail, the CAM QUEUE and its 3D views; each section a card.
    /// </summary>
    public class HelpControl : UserControl
    {
        static readonly string[][] Sections =
        {
            new[] { "Start here",
                "1. Start Redacted BO2. 2. Pre-Production > Setup: it shows where it found the game and what this PC is missing. Most tools need the " +
                "RipperBridge plugin in the game's Plugins folder (Setup can install it; close the game first). 3. Go to the theater by its menus and " +
                "start one film there, once after every game start: films loaded afterwards keep their jump points, and seeking needs them. " +
                "Ctrl+Alt+D brings the Designer back from anywhere, also while the game holds the mouse." },
            new[] { "Getting around",
                "The start screen is the three stages of making a film: Pre-Production (this PC), Principal Vegaography (the game: films, looks, " +
                "cameras, recording) and Post-Production (3D in Blender, in a later version). Click a stage to open it. The rail on the left lists " +
                "the open stage's tools in working order: Film, Look, Control, Shoot. The pills at the top show the tether to the game: " +
                "Redacted and RipperBridge; green is connected. Re-attach makes the tether again after the game restarts. Help is F1 from anywhere; " +
                "the logo (or Ctrl+Home) goes back to the start screen. The coloured button on a page is its main action (maroon by day, lilac " +
                "by night). By day the Designer is light, as reluduali.com's card screen is; by night it is dark (Dynamic); Setup > Appearance picks Dark, " +
                "Light or Dynamic." },
            new[] { "Cam Queue: tonight's cameras",
                "Place markers in the theater's Edit Camera (F inserts one), then SAVE MY MARKERS: they become a CINE row - the same path the game's " +
                "own dolly flies - and the game's markers are never changed. POV in / POV out mark a player's own view; Add POV take adds it. Each row " +
                "gets its kind's defaults (Row defaults: CFG, fps, time before and after, HUD, film mode) unless it has its own. Check finds every " +
                "demo, path and CFG now rather than at 3 am; Rehearse runs the night without recording; RUN QUEUE records it all, then quits the game." },
            new[] { "The 3D views",
                "The path loops in every view as soon as two markers are placed, at the speed you set. + View adds one: through the camera (the " +
                "loop, 1920x808 with thirds), a fixed side (top, front, right, back) or free. Each view has its own map look - colour, see-through, " +
                "wireframe (a slice of the map around the path) or no map - and the person button shows, fades or hides the players. The pin keeps a " +
                "view every time the Designer opens. Double-click a view's header to make it the big one; the pop-out button puts it in a window of its " +
                "own. Click a view, then Blender's numpad: 1 front, 3 right, 7 top (Ctrl: the other side), 5 orthographic, 0 through the camera, " +
                "period frames the path. Drag a camera marker to move it, Alt+drag to aim it, Ctrl+Z to undo." },
            new[] { "Live sliders (Fog, Sun, SSAO, DOF, Look, Theater, Custom)",
                "Every slider acts while you drag it; click anywhere on a slider to jump there. Type a number in the box beside a slider for an exact " +
                "value (Enter). The toolbar: presets first (pick, Load, Save as), then files (Open file takes a .cfg; Copy as CFG), then the game " +
                "(Read from game fills the sliders with what it holds; Reset puts the game's own values back). A coloured square opens the colour " +
                "wheel. A film or map load puts the map's own fog back: send your look again after a load." },
            new[] { "Moves and the Timeline",
                "Record moves, play the film and move sliders: each move is stamped with the film's own clock. Play moves: the sliders follow those " +
                "stamps whenever the film passes them again, also while the batch records. Both on: a second pass, recording over the sliders you touch " +
                "while the others play. Timeline shows the moves as curves: click to add a key, drag to move one, right-click to remove, wheel to zoom, " +
                "Shift+wheel to scroll, double-click to send the game there. Save moves / Load moves keep them. In recordings the moves are exact: the " +
                "batch writes them for every frame (a .params.txt beside the video) and RipperBridge applies each one on its own frame." },
            new[] { "Fog's blend modes",
                "At the bottom of Fog: Fog with a blend mode mixes a fog in by distance in a mode the map's fog never had (normal, add, screen, " +
                "multiply, overlay, soft light, colour dodge, colour burn; the Blend buttons pick one), with its colour, opacity, start and end (or an " +
                "exponential curve by density) and how much covers the sky. You see it at once; recordings leave it out until you tick Lock it in place. " +
                "Locked, it goes into the takes and the world pass, never the players pass. Over the finished frame puts it after the game's bloom and " +
                "colour grade. Reset takes it away with the rest." },
            new[] { "Fog, Sun, SSAO, DOF",
                "Fog writes the game's fog record: distance, height fog and the sun haze. Sun: light, colour (the game keeps only its hue; brightness " +
                "is Sun light), direction, flare, sky temperature, brightness and rotation. DOF needs its switch on (it turns on r_dof_tweak, without which " +
                "the game ignores the values). SSAO shows most on close walls and the gun." },
            new[] { "Theater",
                "Live passes for the edit, while the film plays: No HUD (the kill feed goes too), No gun (first person), FOV, Blur, Far clip, and " +
                "Sky off: the background colour shows instead, for a green sky (props that are models go too). Depth, Clean plate, No players and " +
                "Players only are the Batch's passes." },
            new[] { "CFG",
                "Type a command or a setting: the box completes over every setting and command the game has and shows its type, default and range. " +
                "Enter sends; Up and Down go through what you sent; Read shows what the game holds now. Timescale buttons. The CFG box sends many lines " +
                "at once; the library on the right lists your .cfg files (add folders, double-click a file to open it)." },
            new[] { "Custom",
                "New slider: name any number setting; the slider takes the game's own range. Keys: two keys nudge it while the game is in front (the " +
                "game still gets the keys). Learn MIDI: press it, then move a fader or knob on your MIDI device (open the device first)." },
            new[] { "Demos",
                "The films in the game's theater folder (or any folder). Search and the map filter narrow the list. Load in theater loads one safely " +
                "(never two loads within 10 seconds: that crashes the game). Play / pause, End playback, Go to a time (1:57 as the theater clock shows " +
                "it, or a tick). Bookmarks: Mark here names the moment; double-click a bookmark to go back to it." },
            new[] { "Batch",
                "Records a list of takes by itself: it loads each film, goes to the player, seeks, records, and moves on. The recorder steps the film " +
                "one frame at a time (nothing is dropped, at any frame rate) and sends each frame to ffmpeg, which you install yourself. Passes: Depth " +
                "(a 16-bit PNG sequence of the scene's depth; the distance beside it is black) and Clean plate (the scene before bloom, the colour grade " +
                "and the HUD) come with the take; No players (\"<take> - world\": the players, their guns and their gear gone) and Players only " +
                "(\"<take> - players\": the players with their guns, on black) record the same ticks again after it, frame for frame. Dry run tries " +
                "everything except the recording. Each take's video can go to the Lumit folder (Contest)." },
            new[] { "Contest",
                "For reluduali.com's contests: in Lumit's Reluduali panel press Record > Link the Console, type the code here (Link...). From then on " +
                "the Designer reports in-game work and queued recording at the contest's rates, about once a minute; the page lists every timer message it " +
                "sends. The Lumit folder: choose the same folder you chose in the panel's Watch a folder; each finished take is copied there whole." },
            new[] { "Sky",
                "Sky installs a set of sky images into the game (restart the game after); what was there is moved aside, never deleted." },
            new[] { "If something goes wrong",
                "The Designer catches its own errors and keeps running; ReludualiConsole.log beside it says what happened. If the game restarts, press " +
                "Re-attach. If a seek or Go to does not land, go to the theater by its menus once and load the film again." },
        };

        /// <summary>Mycelium 2026-10-17: "make sure the console has links to Reluduali.com".</summary>
        static FrameworkElement Links()
        {
            var p = new WrapPanel();
            p.Children.Add(Ui.Primary(Ui.Button("reluduali.com", () => Ui.OpenWeb(TimerLink.DefaultServer), "The website: contests, friends, films. Sign in from Contest > Account.")));
            p.Children.Add(Ui.Button("The Designer's source", () => Ui.OpenWeb("https://github.com/HarryShire/reluduali"), "github.com/HarryShire/reluduali: the code, the guide, the releases"));
            p.Children.Add(Ui.Button("Lumit", () => Ui.OpenWeb("https://github.com/luminalmvm/Lumit"), "github.com/luminalmvm/Lumit: the editor the Reluduali panel runs in (luminalmvm's)"));
            return p;
        }

        public HelpControl()
        {
            Foreground = (Brush)Application.Current.FindResource("Text");
            var cols = new Grid { MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left };
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel();
            var right = new StackPanel();
            Grid.SetColumn(right, 2);
            cols.Children.Add(left); cols.Children.Add(right);
            left.Children.Add(Ui.Card("Links", "In your browser.", Links()));
            for (int i = 0; i < Sections.Length; i++)
            {
                var body = new TextBlock { Text = Sections[i][1], TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
                (i % 2 == 0 ? left : right).Children.Add(Ui.Card(Sections[i][0], null, body));
            }
            Content = new ScrollViewer { Content = new Border { Child = cols, Margin = new Thickness(12, 2, 12, 12) }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
