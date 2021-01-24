using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;
using MahApps.Metro.Controls.Dialogs;
using SourcepawnCondenser;
using SourcepawnCondenser.SourcemodDefinition;
using SPCode.Utils.SPSyntaxTidy;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Xceed.Wpf.AvalonDock.Layout;
using Timer = System.Timers.Timer;

namespace SPCode.UI.Components
{
    /// <summary>
    ///     Interaction logic for EditorElement.xaml
    /// </summary>
    public partial class EditorElement : UserControl
    {
        #region Variables

        private readonly BracketHighlightRenderer bracketHighlightRenderer;
        private readonly SPBracketSearcher bracketSearcher;
        private readonly ColorizeSelection colorizeSelection;

        private readonly Storyboard FadeJumpGridIn;
        private readonly Storyboard FadeJumpGridOut;
        private readonly SPFoldingStrategy foldingStrategy;

        private readonly Timer regularyTimer;
        private string _FullFilePath = "";

        private bool _NeedsSave;
        public Timer AutoSaveTimer;

        private FileSystemWatcher fileWatcher;

        public FoldingManager foldingManager;

        private bool isBlock;

        private bool JumpGridIsOpen;

        private double LineHeight;
        public new LayoutDocument Parent;
        private bool SelectionIsHighlighted;
        private bool WantFoldingUpdate;

        private Timer parseTimer;
        private SMDefinition currentSmDef;
        #endregion

        #region Constructors and Initializers
        public EditorElement()
        {
            InitializeComponent();
        }

        public EditorElement(string filePath)
        {
            InitializeComponent();

            bracketSearcher = new SPBracketSearcher();
            bracketHighlightRenderer = new BracketHighlightRenderer(editor.TextArea.TextView);
            editor.TextArea.IndentationStrategy = new EditorIndentationStrategy();

            FadeJumpGridIn = (Storyboard)Resources["FadeJumpGridIn"];
            FadeJumpGridOut = (Storyboard)Resources["FadeJumpGridOut"];

            editor.CaptureMouse();

            KeyDown += EditorElement_KeyDown;

            editor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            editor.TextArea.SelectionChanged += TextArea_SelectionChanged;
            editor.TextArea.PreviewKeyDown += TextArea_PreviewKeyDown;

            editor.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(TextArea_MouseDown), true);

            editor.PreviewMouseWheel += PrevMouseWheel;
            editor.MouseDown += editor_MouseDown;
            editor.Loaded += editor_Loaded;

            editor.TextArea.TextEntered += TextArea_TextEntered;
            editor.TextArea.TextEntering += TextArea_TextEntering;
            var fInfo = new FileInfo(filePath);
            if (fInfo.Exists)
            {
                fileWatcher = new FileSystemWatcher(fInfo.DirectoryName ?? throw new NullReferenceException())
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
                    Filter = "*" + fInfo.Extension
                };
                fileWatcher.Changed += fileWatcher_Changed;
                fileWatcher.EnableRaisingEvents = true;
            }
            else
            {
                fileWatcher = null;
            }

            _FullFilePath = filePath;
            editor.Options.ConvertTabsToSpaces = false;
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.Options.HighlightCurrentLine = true;
            editor.Options.AllowScrollBelowDocument = true;
            editor.Options.ShowSpaces = Program.OptionsObject.Editor_ShowSpaces;
            editor.Options.ShowTabs = Program.OptionsObject.Editor_ShowTabs;
            editor.Options.IndentationSize = Program.OptionsObject.Editor_IndentationSize;
            editor.TextArea.SelectionCornerRadius = 0.0;
            editor.Options.ConvertTabsToSpaces = Program.OptionsObject.Editor_ReplaceTabsToWhitespace;

            Brush currentLineBackground = new SolidColorBrush(Color.FromArgb(0x20, 0x88, 0x88, 0x88));
            Brush currentLinePenBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x88, 0x88, 0x88));
            currentLinePenBrush.Freeze();
            var currentLinePen = new Pen(currentLinePenBrush, 1.0);
            currentLineBackground.Freeze();
            currentLinePen.Freeze();
            editor.TextArea.TextView.CurrentLineBackground = currentLineBackground;
            editor.TextArea.TextView.CurrentLineBorder = currentLinePen;

            editor.FontFamily = new FontFamily(Program.OptionsObject.Editor_FontFamily);
            editor.WordWrap = Program.OptionsObject.Editor_WordWrap;
            UpdateFontSize(Program.OptionsObject.Editor_FontSize, false);

            colorizeSelection = new ColorizeSelection();
            editor.TextArea.TextView.LineTransformers.Add(colorizeSelection);

            LoadAutoCompletes();

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var reader = FileReader.OpenStream(fs, Encoding.UTF8);
                var source = reader.ReadToEnd();
                source = source.Replace("\r\n", "\n").Replace("\r", "\n")
                    .Replace("\n", "\r\n"); //normalize line endings
                editor.Text = source;
            }

            _NeedsSave = false;

            Language_Translate(true); //The Fontsize and content must be loaded

            var encoding = new UTF8Encoding(false);
            editor.Encoding = encoding; //let them read in whatever encoding they want - but save in UTF8

            foldingManager = FoldingManager.Install(editor.TextArea);
            foldingStrategy = new SPFoldingStrategy();
            foldingStrategy.UpdateFoldings(foldingManager, editor.Document);

            regularyTimer = new Timer(500.0);
            regularyTimer.Elapsed += regularyTimer_Elapsed;
            regularyTimer.Start();

            AutoSaveTimer = new Timer();
            AutoSaveTimer.Elapsed += AutoSaveTimer_Elapsed;
            StartAutoSaveTimer();

            CompileBox.IsChecked = filePath.EndsWith(".sp");
        }

        private void editor_Loaded(object sender, RoutedEventArgs e)
        {
            ParseIncludes(sender, e);
        }

        #endregion

        #region Miscellaneous

        /// <summary>
        /// Gets the current file's full path.
        /// </summary>
        public string FullFilePath
        {
            get => _FullFilePath;
            set
            {
                var fInfo = new FileInfo(value);
                _FullFilePath = fInfo.FullName;
                Parent.Title = fInfo.Name;
                if (fileWatcher != null) fileWatcher.Path = fInfo.DirectoryName;
            }
        }

        /// <summary>
        /// Adds an asterisk to a file's title if it's unsaved.
        /// </summary>
        public bool NeedsSave
        {
            get => _NeedsSave;
            set
            {
                if (!(value ^ _NeedsSave)) //when not changed
                    return;
                _NeedsSave = value;
                if (Parent != null)
                {
                    if (_NeedsSave)
                        Parent.Title = "*" + Parent.Title;
                    else
                        Parent.Title = Parent.Title.Trim('*');
                }
            }
        }

        /// <summary>
        /// Gets the full word the caret is standing on.
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        private string GetWordAtMousePosition(MouseEventArgs e)
        {
            var mousePosition = editor.GetPositionFromPoint(e.GetPosition(this));

            if (mousePosition == null)
                return string.Empty;

            var line = mousePosition.Value.Line;
            var column = mousePosition.Value.Column;
            var offset = editor.TextArea.Document.GetOffset(line, column);

            if (offset >= editor.TextArea.Document.TextLength)
                offset--;

            var offsetStart = TextUtilities.GetNextCaretPosition(editor.TextArea.Document, offset,
                LogicalDirection.Backward, CaretPositioningMode.WordBorder);
            var offsetEnd = TextUtilities.GetNextCaretPosition(editor.TextArea.Document, offset,
                LogicalDirection.Forward, CaretPositioningMode.WordBorder);

            if (offsetEnd == -1 || offsetStart == -1)
                return string.Empty;

            var currentChar = editor.TextArea.Document.GetText(offset, 1);

            if (string.IsNullOrWhiteSpace(currentChar))
                return string.Empty;

            return editor.TextArea.Document.GetText(offsetStart, offsetEnd - offsetStart);
        }

        /// <summary>
        /// If specified in settings, starts the auto-save timer.
        /// </summary>
        public void StartAutoSaveTimer()
        {
            if (Program.OptionsObject.Editor_AutoSave)
            {
                if (AutoSaveTimer.Enabled) AutoSaveTimer.Stop();
                AutoSaveTimer.Interval = 1000.0 * Program.OptionsObject.Editor_AutoSaveInterval;
                AutoSaveTimer.Start();
            }
        }

        /// <summary>
        /// Used by the Go To Symbol Definition function to check if the provided definition (symbol) exists within the parsed includes.
        /// </summary>
        /// <param name="smDef"></param>
        /// <param name="word"></param>
        /// <param name="e"></param>
        /// <param name="currentFile"></param>
        /// <returns></returns>
        private SMBaseDefinition MatchDefinition(SMDefinition smDef, string word, MouseButtonEventArgs e, bool currentFile = false)
        {
            if (smDef == null)
                return null;

            var mousePosition = editor.GetPositionFromPoint(e.GetPosition(this));

            if (mousePosition == null)
                return null;

            // Get the caret's position
            var line = mousePosition.Value.Line;
            var column = mousePosition.Value.Column;
            var offset = editor.TextArea.Document.GetOffset(line, column);

            // In first place, instances a BaseDefinition fetching the supplied symbol from functions
            var sm = (SMBaseDefinition)smDef.Functions.FirstOrDefault(i => i.Name == word);

            // If passed currentFile, will check in the same file
            if (currentFile)
            {
                sm ??= smDef.Functions.FirstOrDefault(func => func.Index <= offset && offset <= func.EndPos)
                    ?.FuncVariables?.FirstOrDefault(i => i.Name.Equals(word));
            }

            // Checks and assigns now a variable to sm (if found) only if sm came null
            sm ??= smDef.Variables.FirstOrDefault(i =>
                i.Name.Equals(word));

            // Same now with constants
            sm ??= smDef.Constants.FirstOrDefault(i =>
                i.Name.Equals(word));

            // And so on
            sm ??= smDef.Defines.FirstOrDefault(i => i.Name.Equals(word));

            sm ??= smDef.Enums.FirstOrDefault(i => i.Name.Equals(word));

            if (sm == null)
            {
                foreach (var smEnum in smDef.Enums)
                {
                    var str = smEnum.Entries.FirstOrDefault(
                        i => i.Equals(word));

                    if (str == null) continue;
                    sm = smEnum;
                    break;
                }
            }


            //TODO: Match EnumStruct and MethodMaps Fields and Methods
            sm ??= smDef.EnumStructs.FirstOrDefault(i =>
                i.Name.Equals(word, StringComparison.InvariantCultureIgnoreCase));

            sm ??= smDef.Methodmaps.FirstOrDefault(
                i => i.Name.Equals(word, StringComparison.InvariantCultureIgnoreCase));

            sm ??= smDef.Structs.FirstOrDefault(i => i.Name.Equals(word, StringComparison.InvariantCultureIgnoreCase));

            sm ??= smDef.Typedefs.FirstOrDefault(i => i.Name.Equals(word, StringComparison.InvariantCultureIgnoreCase));

            // Debug.Print($"Function {word} found with {sm}!");

            return sm;
        }

        /// <summary>
        /// Handles ctrl+g to open Go To Line popup.
        /// </summary>
        public void ToggleJumpGrid()
        {
            if (JumpGridIsOpen)
            {
                FadeJumpGridOut.Begin();
                JumpGridIsOpen = false;
            }
            else
            {
                FadeJumpGridIn.Begin();
                JumpGridIsOpen = true;
                JumpNumber.Focus();
                JumpNumber.SelectAll();
            }
        }

        /// <summary>
        /// Handles file save.
        /// </summary>
        /// <param name="force"></param>
        public void Save(bool force = false)
        {
            if (_NeedsSave || force)
            {
                if (fileWatcher != null) fileWatcher.EnableRaisingEvents = false;
                try
                {
                    using var fs = new FileStream(_FullFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    editor.Save(fs);
                }
                catch (Exception e)
                {
                    MessageBox.Show(Program.MainWindow,
                        Program.Translations.GetLanguage("DSaveError") + Environment.NewLine + "(" + e.Message + ")",
                        Program.Translations.GetLanguage("SaveError"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                NeedsSave = false;
                if (fileWatcher != null) fileWatcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>
        /// Triggers when font size is changed.
        /// </summary>
        /// <param name="size"></param>
        /// <param name="updateLineHeight"></param>
        public void UpdateFontSize(double size, bool updateLineHeight = true)
        {
            if (size > 2 && size < 31)
            {
                editor.FontSize = size;
                StatusLine_FontSize.Text = size.ToString("n0") + $" {Program.Translations.GetLanguage("PtAbb")}";
            }

            if (updateLineHeight) LineHeight = editor.TextArea.TextView.DefaultLineHeight;
        }

        /// <summary>
        /// Handles toggle comment shortcut to comment lines.
        /// </summary>
        public void ToggleCommentOnLine()
        {
            var line = editor.Document.GetLineByOffset(editor.CaretOffset);
            var lineText = editor.Document.GetText(line);
            var leadingWhiteSpaces = 0;
            foreach (var l in lineText)
                if (char.IsWhiteSpace(l))
                    leadingWhiteSpaces++;
                else
                    break;

            lineText = lineText.Trim();
            if (lineText.Length > 1)
            {
                if (lineText[0] == '/' && lineText[1] == '/')
                    editor.Document.Remove(line.Offset + leadingWhiteSpaces, 2);
                else
                    editor.Document.Insert(line.Offset + leadingWhiteSpaces, "//");
            }
            else
            {
                editor.Document.Insert(line.Offset + leadingWhiteSpaces, "//");
            }
        }

        /// <summary>
        /// Handles ctrl+d for duplicating lines.
        /// </summary>
        /// <param name="down"></param>
        private void DuplicateLine(bool down)
        {
            var line = editor.Document.GetLineByOffset(editor.CaretOffset);
            var lineText = editor.Document.GetText(line);
            editor.Document.Insert(line.Offset, lineText + Environment.NewLine);
            if (down) editor.CaretOffset -= line.Length + 1;
        }

        /// <summary>
        /// Handles ctrl+arrows to move lines around
        /// </summary>
        /// <param name="down"></param>
        private void MoveLine(bool down)
        {
            var line = editor.Document.GetLineByOffset(editor.CaretOffset);
            if (down)
            {
                if (line.NextLine == null)
                {
                    editor.Document.Insert(line.Offset, Environment.NewLine);
                }
                else
                {
                    var lineText = editor.Document.GetText(line.NextLine);
                    editor.Document.Remove(line.NextLine.Offset, line.NextLine.TotalLength);
                    editor.Document.Insert(line.Offset, lineText + Environment.NewLine);
                }
            }
            else
            {
                if (line.PreviousLine == null)
                {
                    editor.Document.Insert(line.Offset + line.Length, Environment.NewLine);
                }
                else
                {
                    var insertOffset = line.PreviousLine.Offset;
                    var relativeCaretOffset = editor.CaretOffset - line.Offset;
                    var lineText = editor.Document.GetText(line);
                    editor.Document.Remove(line.Offset, line.TotalLength);
                    editor.Document.Insert(insertOffset, lineText + Environment.NewLine);
                    editor.CaretOffset = insertOffset + relativeCaretOffset;
                }
            }
        }

        /// <summary>
        /// Handles file closing.
        /// </summary>
        /// <param name="ForcedToSave"></param>
        /// <param name="CheckSavings"></param>
        public async void Close(bool ForcedToSave = false, bool CheckSavings = true)
        {
            regularyTimer.Stop();
            regularyTimer.Close();
            if (fileWatcher != null)
            {
                fileWatcher.EnableRaisingEvents = false;
                fileWatcher.Dispose();
                fileWatcher = null;
            }

            if (CheckSavings)
                if (_NeedsSave)
                {
                    if (ForcedToSave)
                    {
                        Save();
                    }
                    else
                    {
                        var title = $"{Program.Translations.GetLanguage("SavingFile")} '" + Parent.Title.Trim('*') +
                                    "'";
                        var Result = await Program.MainWindow.ShowMessageAsync(title, "",
                            MessageDialogStyle.AffirmativeAndNegative, Program.MainWindow.MetroDialogOptions);
                        if (Result == MessageDialogResult.Affirmative) Save();
                    }
                }

            Program.MainWindow.EditorsReferences.Remove(this);
            // var childs = Program.MainWindow.DockingPaneGroup.Children;
            //  foreach (var c in childs) (c as LayoutDocumentPane)?.Children.Remove(Parent);

            Parent = null; //to prevent a ring depency which disables the GC from work
            Program.MainWindow.UpdateWindowTitle();
        }

        /// <summary>
        /// Used by the timer that highlights words on selection to check if the selection is valid.
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        private bool IsValidSearchSelectionString(string s)
        {
            var length = s.Length;
            for (var i = 0; i < length; ++i)
                if (!((s[i] >= 'a' && s[i] <= 'z') || (s[i] >= 'A' && s[i] <= 'Z') || (s[i] >= '0' && s[i] <= '9') ||
                      s[i] == '_'))
                    return false;
            return true;
        }

        /// <summary>
        /// Handles menu items translations.
        /// </summary>
        /// <param name="Initial"></param>
        public void Language_Translate(bool Initial = false)
        {
            if (Program.Translations.IsDefault) return;
            MenuC_Undo.Header = Program.Translations.GetLanguage("Undo");

            MenuC_Redo.Header = Program.Translations.GetLanguage("Redo");

            MenuC_Cut.Header = Program.Translations.GetLanguage("Cut");

            MenuC_Copy.Header = Program.Translations.GetLanguage("Copy");

            MenuC_Paste.Header = Program.Translations.GetLanguage("Paste");

            MenuC_SelectAll.Header = Program.Translations.GetLanguage("SelectAll");
            CompileBox.Content = Program.Translations.GetLanguage("Compile");
            if (!Initial)
            {
                StatusLine_Coloumn.Text =
                    $"{Program.Translations.GetLanguage("ColAbb")} {editor.TextArea.Caret.Column}";
                StatusLine_Line.Text = $"{Program.Translations.GetLanguage("LnAbb")} {editor.TextArea.Caret.Line}";
                StatusLine_FontSize.Text =
                    editor.FontSize.ToString("n0") + $" {Program.Translations.GetLanguage("PtAbb")}";
            }
        }

        #endregion

        #region Form Events

        /// <summary>
        /// Handles Go To Symbol Definition (ctrl+click)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TextArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!Keyboard.IsKeyDown(Key.LeftCtrl)) return;

            var word = GetWordAtMousePosition(e);
            Debug.Print($"The word: {word}");
            if (word.Trim().Length == 0) return;

            e.Handled = true;

            // First try to match variables in the current file

            var sm = MatchDefinition(currentSmDef, word, e, true);
            if (sm != null)
            {
                editor.TextArea.Caret.Offset = sm.Index;
                editor.TextArea.Caret.BringCaretToView();
                await Task.Delay(100);
                editor.TextArea.Selection = Selection.Create(editor.TextArea, sm.Index, sm.Index + sm.Length);
                return;
            }

            // Now let's search across all scripting directories

            sm = MatchDefinition(Program.Configs[Program.SelectedConfig].GetSMDef(), word, e);
            if (sm != null)
            {
                var config = Program.Configs[Program.SelectedConfig].SMDirectories;

                foreach (var cfg in config)
                {
                    var file = Path.GetFullPath(Path.Combine(cfg, "include", sm.File)) + ".inc";
                    await Task.Delay(100);
                    var result = Program.MainWindow.TryLoadSourceFile(file, true, false, true);
                    if (!result)
                    {
                        Debug.Print($"File {file} not found!");
                        continue;
                    }
                    var newEditor = Program.MainWindow.GetCurrentEditorElement();
                    Debug.Assert(newEditor != null);
                    newEditor.editor.TextArea.Caret.Offset = sm.Index;
                    newEditor.editor.TextArea.Caret.BringCaretToView();
                    newEditor.editor.TextArea.Selection = Selection.Create(newEditor.editor.TextArea, sm.Index, sm.Index + sm.Length);
                    return;
                }
            }
        }

        /// <summary>
        /// Toggle go to line popup.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void EditorElement_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.G)
                if (Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightAlt))
                {
                    ToggleJumpGrid();
                    e.Handled = true;
                }
        }

        /// <summary>
        /// Auto-save timer callback.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AutoSaveTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (NeedsSave)
                Dispatcher.Invoke(() => { Save(); });
        }

        /// <summary>
        /// No idea wtf.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void JumpNumberKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                JumpToNumber(null, null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Jumps to the line in go to line popup
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void JumpToNumber(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(JumpNumber.Text, out var num))
            {
                if (LineJump.IsChecked != null && LineJump.IsChecked.Value)
                {
                    num = Math.Max(1, Math.Min(num, editor.LineCount));
                    var line = editor.Document.GetLineByNumber(num);
                    if (line != null)
                    {
                        editor.ScrollToLine(num);
                        editor.Select(line.Offset, line.Length);
                        editor.CaretOffset = line.Offset;
                    }
                }
                else
                {
                    num = Math.Max(0, Math.Min(num, editor.Text.Length));
                    var line = editor.Document.GetLineByOffset(num);
                    if (line != null)
                    {
                        editor.ScrollTo(line.LineNumber, 0);
                        editor.CaretOffset = num;
                    }
                }
            }

            ToggleJumpGrid();
            editor.Focus();
        }

        /// <summary>
        /// File watcher callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void fileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (e == null) return;
            if (e.FullPath == _FullFilePath)
            {
                bool reloadFile;
                if (_NeedsSave)
                {
                    var result = MessageBox.Show(
                        string.Format(Program.Translations.GetLanguage("DFileChanged"), _FullFilePath) +
                        Environment.NewLine + Program.Translations.GetLanguage("FileTryReload"),
                        Program.Translations.GetLanguage("FileChanged"), MessageBoxButton.YesNo,
                        MessageBoxImage.Asterisk);
                    reloadFile = result == MessageBoxResult.Yes;
                }
                else //when the user didnt changed anything, we just reload the file since we are intelligent...
                {
                    reloadFile = true;
                }

                if (reloadFile)
                    Dispatcher.Invoke(() =>
                    {
                        FileStream stream;
                        var IsNotAccessed = true;
                        while (IsNotAccessed)
                        {
                            try
                            {
                                using (stream = new FileStream(_FullFilePath, FileMode.OpenOrCreate))
                                {
                                    editor.Load(stream);
                                    NeedsSave = false;
                                    IsNotAccessed = false;
                                }
                            }
                            catch (Exception)
                            {
                                // ignored
                            }

                            Thread.Sleep(
                                100); //dont include System.Threading in the using directives, cause its onlyused once and the Timer class will double
                        }
                    });
            }
        }

        /// <summary>
        /// Highlight timer callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void regularyTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (editor.SelectionLength > 0 && editor.SelectionLength < 50)
                {
                    var selectionString = editor.SelectedText;
                    if (IsValidSearchSelectionString(selectionString))
                    {
                        colorizeSelection.SelectionString = selectionString;
                        colorizeSelection.HighlightSelection = true;
                        SelectionIsHighlighted = true;
                        editor.TextArea.TextView.Redraw();
                    }
                    else
                    {
                        colorizeSelection.HighlightSelection = false;
                        colorizeSelection.SelectionString = string.Empty;
                        if (SelectionIsHighlighted)
                        {
                            editor.TextArea.TextView.Redraw();
                            SelectionIsHighlighted = false;
                        }
                    }
                }
                else
                {
                    colorizeSelection.HighlightSelection = false;
                    colorizeSelection.SelectionString = string.Empty;
                    if (SelectionIsHighlighted)
                    {
                        editor.TextArea.TextView.Redraw();
                        SelectionIsHighlighted = false;
                    }
                }
            });
            if (WantFoldingUpdate)
            {
                WantFoldingUpdate = false;
                try //this "solves" a racing-conditions error - i wasnt able to fix it till today.. 
                {
                    Dispatcher.Invoke(() => { foldingStrategy.UpdateFoldings(foldingManager, editor.Document); });
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        /// <summary>
        /// Text change callback to trigger NeedsSave.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void editor_TextChanged(object sender, EventArgs e)
        {
            WantFoldingUpdate = true;
            NeedsSave = true;
        }

        /// <summary>
        /// Fires on carent moving to evaluate intellisense and other things
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Caret_PositionChanged(object sender, EventArgs e)
        {
            StatusLine_Coloumn.Text = $"{Program.Translations.GetLanguage("ColAbb")} {editor.TextArea.Caret.Column}";
            StatusLine_Line.Text = $"{Program.Translations.GetLanguage("LnAbb")} {editor.TextArea.Caret.Line}";
            EvaluateIntelliSense();
            var result = bracketSearcher.SearchBracket(editor.Document, editor.CaretOffset);
            bracketHighlightRenderer.SetHighlight(result);


            if (!Program.OptionsObject.Program_DynamicISAC || Program.MainWindow == null) return;

            if (parseTimer != null)
            {
                parseTimer.Enabled = false;
                parseTimer.Close();
            }

            parseTimer = new Timer(200)
            {
                AutoReset = false,
                Enabled = true,
            };
            parseTimer.Elapsed += ParseIncludes;
        }

        /// <summary>
        /// Fired on a new editor loaded to parse includes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ParseIncludes(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (Program.MainWindow == null) return;

                var ee = Program.MainWindow.GetAllEditorElements();
                var ce = Program.MainWindow.GetCurrentEditorElement();

                var caret = -1;

                if (ee == null) return;

                var definitions = new SMDefinition[ee.Length];
                List<SMFunction> currentFunctions = null;
                for (var i = 0; i < ee.Length; ++i)
                {
                    var el = ee[i];
                    var fInfo = new FileInfo(el.FullFilePath);
                    var text = el.editor.Document.Text;
                    if (fInfo.Extension.Trim('.').ToLowerInvariant() == "inc")
                        definitions[i] =
                            new Condenser(text
                                , fInfo.Name).Condense();

                    if (fInfo.Extension.Trim('.').ToLowerInvariant() == "sp")
                        if (el.IsLoaded)
                        {
                            caret = el.editor.CaretOffset;
                            definitions[i] =
                                new Condenser(text, fInfo.Name)
                                    .Condense();
                            currentFunctions = definitions[i].Functions;
                            if (el == ce)
                            {
                                currentSmDef = definitions[i];
                                var caret1 = caret;
                                currentSmDef.currentFunction =
                                    currentFunctions.FirstOrDefault(
                                        func => func.Index <= caret1 && caret1 <= func.EndPos);

                            }
                        }
                }

                var smDef = Program.Configs[Program.SelectedConfig].GetSMDef()
                    .ProduceTemporaryExpandedDefinition(definitions, caret, currentFunctions);
                var smFunctions = smDef.Functions.ToArray();
                var acNodes = smDef.ProduceACNodes();
                var isNodes = smDef.ProduceISNodes();

                // Lags the hell out when typing a lot.
                ce.editor.SyntaxHighlighting = new AeonEditorHighlighting(smDef);

                foreach (var el in ee)
                {
                    if (el == ce)
                    {
                        Debug.Assert(ce != null, nameof(ce) + " != null");
                        if (ce.ISAC_Open) continue;
                    }


                    el.InterruptLoadAutoCompletes(smDef.FunctionStrings, smFunctions, acNodes,
                        isNodes, smDef.Methodmaps.ToArray(), smDef.Variables.ToArray());
                }
            });
        }

        /// <summary>
        /// On text entered
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TextArea_TextEntered(object sender, TextCompositionEventArgs e)
        {
            if (Program.OptionsObject.Editor_ReformatLineAfterSemicolon)
                if (e.Text == ";")
                    if (editor.CaretOffset >= 0)
                    {
                        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
                        var text = editor.Document.GetText(line);

                        // TODO: Poor way to fix this but atm I have no idea on how to fix this properly
                        if (!text.Contains("for"))
                        {
                            var leadingIndentation =
                                editor.Document.GetText(TextUtilities.GetLeadingWhitespace(editor.Document, line));
                            var newLineStr = leadingIndentation +
                                             SPSyntaxTidy.TidyUp(text).Trim();
                            editor.Document.Replace(line, newLineStr);
                        }
                    }

            switch (e.Text)
            {
                case "\n":
                    if (!isBlock)
                        break;

                    editor.TextArea.Caret.Line -= 1;
                    editor.TextArea.Caret.Column += Program.Indentation.Length;
                    isBlock = false;
                    break;
                case "}":
                    // Seems like this is not required
                    // editor.TextArea.IndentationStrategy.IndentLine(editor.Document, editor.Document.GetLineByOffset(editor.CaretOffset));
                    foldingStrategy.UpdateFoldings(foldingManager, editor.Document);
                    break;
                case "(":
                case "[":
                case "{":
                    if (Program.OptionsObject.Editor_AutoCloseBrackets)
                    {
                        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
                        var lineText = editor.Document.GetText(line);

                        // Don't auto close brackets when the user is in a comment or in a string or a text is selected.
                        if ((editor.SelectionLength == 0 &&
                            lineText[0] == '/' && lineText[1] == '/') ||
                            editor.Document.GetText(line.Offset, editor.CaretOffset - line.Offset).Count(c => c == '\"') % 2 == 1 ||
                            (line.LineNumber != 1 && editor.Document.GetText(line.Offset - 3, 1) == "\\"))
                            break;

                        // Getting the char ascii code with int cast and the string pos 0 (the char it self),
                        // if it's a ( i need to add 1 to get the ascii code for closing bracket
                        // for [ and { i need to add 2 to get the closing bracket ascii code
                        char closingBracket = (char)((int)e.Text[0] + (e.Text == "(" ? 1 : 2));
                        editor.Document.Insert(editor.CaretOffset, closingBracket.ToString());
                        if (editor.SelectionLength == 0)
                            editor.CaretOffset -= 1;

                        // If it's a code block bracket we need to update the folding
                        if (e.Text == "{")
                            foldingStrategy.UpdateFoldings(foldingManager, editor.Document);
                    }

                    break;
                case "\"":
                case "'":
                    if (Program.OptionsObject.Editor_AutoCloseStringChars)
                    {
                        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
                        var lineText = editor.Document.GetText(line.Offset, editor.CaretOffset - line.Offset);
                        if (editor.SelectionLength > 0 || (lineText.Length > 0 && lineText[Math.Max(lineText.Length - 2, 0)] != '\\'))
                        {
                            editor.Document.Insert(editor.CaretOffset, e.Text);
                            if (editor.SelectionLength == 0)
                                editor.CaretOffset -= 1;
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// On text entering
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TextArea_TextEntering(object sender, TextCompositionEventArgs e)
        {
            switch (e.Text)
            {
                default:
                    return;

                case "\n":
                    if (editor.Document.TextLength < editor.CaretOffset + 1 || editor.CaretOffset < 3)
                        return;

                    var segment = new AnchorSegment(editor.Document, editor.CaretOffset - 1, 2);
                    var text = editor.Document.GetText(segment);
                    if (text == "{}")
                        isBlock = true;
                    return;

                case "(":
                case "[":
                case "{":
                    if (!Program.OptionsObject.Editor_AutoCloseBrackets)
                        return; break;

                case "\"":
                case "'":
                    if (!Program.OptionsObject.Editor_AutoCloseStringChars)
                        return; break;
            }

            var selectionLength = editor.SelectionLength;
            if (selectionLength > 0)
            {
                editor.Document.BeginUpdate();
                editor.Document.Insert(editor.SelectionStart, e.Text);
                editor.CaretOffset = editor.SelectionStart + editor.SelectionLength;
                TextArea_TextEntered(sender, e);
                e.Handled = true;
                editor.Document.EndUpdate();
            }
        }

        /// <summary>
        /// On selection changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TextArea_SelectionChanged(object sender, EventArgs e)
        {
            StatusLine_SelectionLength.Text = $"{Program.Translations.GetLanguage("LenAbb")} {editor.SelectionLength}";
        }

        /// <summary>
        /// Handles scrollwheel - on ctrl pressed, changes font size, otherwise moves to configured scroll speed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PrevMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl))
            {
                UpdateFontSize(editor.FontSize + Math.Sign(e.Delta));
                e.Handled = true;
            }
            else
            {
                if (LineHeight == 0.0) LineHeight = editor.TextArea.TextView.DefaultLineHeight;
                editor.ScrollToVerticalOffset(editor.VerticalOffset -
                                              (Math.Sign((double)e.Delta) * LineHeight *
                                              Program.OptionsObject.Editor_ScrollLines));
                e.Handled = true;
            }

            HideISAC();
        }

        /// <summary>
        /// On right-click pressed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void editor_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            HideISAC();
        }

        /// <summary>
        /// Handles stuff on arrow keys pressed (any of them)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TextArea_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = ISAC_EvaluateKeyDownEvent(e.Key);
            if (!e.Handled
            ) //one could ask why some key-bindings are handled here. Its because spedit sends handled flags for ups&downs and they are therefore not able to processed by the central code.
                if (e.KeyboardDevice.IsKeyDown(Key.LeftCtrl))
                {
                    if (e.KeyboardDevice.IsKeyDown(Key.LeftAlt))
                    {
                        if (e.Key == Key.Down)
                        {
                            DuplicateLine(true);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Up)
                        {
                            DuplicateLine(false);
                            e.Handled = true;
                        }
                    }
                    else
                    {
                        if (e.Key == Key.Down)
                        {
                            MoveLine(true);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Up)
                        {
                            MoveLine(false);
                            e.Handled = true;
                        }
                    }
                }
        }

        /// <summary>
        /// Handles right-click menu.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void HandleContextMenuCommand(object sender, RoutedEventArgs e)
        {
            switch ((string)((MenuItem)sender).Tag)
            {
                case "0":
                    {
                        editor.Undo();
                        break;
                    }
                case "1":
                    {
                        editor.Redo();
                        break;
                    }
                case "2":
                    {
                        editor.Cut();
                        break;
                    }
                case "3":
                    {
                        editor.Copy();
                        break;
                    }
                case "4":
                    {
                        editor.Paste();
                        break;
                    }
                case "5":
                    {
                        editor.SelectAll();
                        break;
                    }
            }
        }

        /// <summary>
        /// Handles right-click menu opening?
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ContextMenu_Opening(object sender, RoutedEventArgs e)
        {
            ((MenuItem)((ContextMenu)sender).Items[0]).IsEnabled = editor.CanUndo;
            ((MenuItem)((ContextMenu)sender).Items[1]).IsEnabled = editor.CanRedo;
        }

        #endregion
    }

    public class ColorizeSelection : DocumentColorizingTransformer
    {
        public bool HighlightSelection;
        public string SelectionString = string.Empty;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (HighlightSelection)
            {
                if (string.IsNullOrWhiteSpace(SelectionString)) return;
                var lineStartOffset = line.Offset;
                var text = CurrentContext.Document.GetText(line);
                var start = 0;
                int index;
                while ((index = text.IndexOf(SelectionString, start, StringComparison.Ordinal)) >= 0)
                {
                    ChangeLinePart(
                        lineStartOffset + index,
                        lineStartOffset + index + SelectionString.Length,
                        element => { element.BackgroundBrush = Brushes.LightGray; });
                    start = index + 1;
                }
            }
        }
    }
}