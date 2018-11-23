namespace ConsoleBuffer
{
    using System;
    using System.ComponentModel;

    public sealed class Buffer : INotifyPropertyChanged
    {
        private readonly SequenceParser parser = new SequenceParser();
        private readonly CircularBuffer<Line> lines = new CircularBuffer<Line>(short.MaxValue);
        private readonly object renderLock = new object();

        private short cursorX;
        private short cursorY;
        private int currentChar;
        /// <summary>
        /// we store X/Y as 0-offset indexes for convenience. escape codes will pass these around as 1-offset (top left is 1,1)
        /// and we'll translate that nonsense where we have to.
        /// </summary>
        public (short X, short Y) CursorPosition => (this.cursorX, this.cursorY);
        public bool CursorVisible { get; private set; }
        public bool CursorBlink { get; private set; }

        private int topVisibleLine;
        private int bottomVisibleLine;

        private int CurrentLine
        {
            get
            {
                return this.topVisibleLine + this.cursorY;
            }
        }

        public short Width { get; set; }
        public short Height { get; set; }

        public string Title { get; private set; }

        public Buffer(short width, short height)
        {
            this.Width = width;
            this.Height = height;
            this.CursorVisible = this.CursorBlink = true;
            this.cursorX = this.cursorY = 0;

            for (var y = 0; y < this.Height; ++y)
            {
                this.lines.PushBack(new Line(null));
            }
            this.topVisibleLine = 0;
            this.bottomVisibleLine = this.Height - 1;
        }

        public void Append(byte[] bytes, int length)
        {
            lock (this.renderLock)
            {
                for (var i = 0;i < length; ++i)
                {
                    if (!this.AppendChar(bytes[i])) continue;

                    switch (this.parser.Append(this.currentChar))
                    {
                    case ParserAppendResult.Render:
                        this.RenderAtCursor(this.currentChar);
                        break;
                    case ParserAppendResult.Complete:
                        this.ExecuteParserCommand();
                        break;
                    case ParserAppendResult.Pending:
                        break;
                    case ParserAppendResult.Invalid:
                        // XXX: we should keep a trailing history of received bytes or something so we can actually log meaningful data.
                        Logger.Verbose("Invalid command sequence in parser.");
                        break;
                    default:
                        throw new InvalidOperationException("unexpected parser result");
                    }
                }
            }
        }

        /// <summary>
        /// Renders the current character at the cursor, advances the cursor, and proceeds to the next line if necessary while scrolling the buffer.
        /// </summary>
        /// <param name="ch"></param>
        private void RenderAtCursor(int ch)
        {
            this.lines[this.CurrentLine].Set(this.cursorX, new Character { Glyph = ch });
            ++this.cursorX;
            if (this.cursorX == this.Width)
            {
                this.cursorX = 0;
                if (this.cursorY == this.Height - 1)
                {
                    this.ScrollDown();
                }
                else
                {
                    ++this.cursorY;
                }
            }
        }

        /// <summary>
        /// Append a single byte to the current character.
        /// </summary>
        /// <returns>true if the current character represents a completed Unicode character</returns>
        private bool AppendChar(byte b)
        {
            // TODO: actual utf-8 parsing.
            this.currentChar = b;
            return true;
        }

        private void ExecuteParserCommand()
        {
            switch (this.parser.Command)
            {
            case Commands.ControlCharacter ctrl:
                this.HandleControlCharacter(ctrl.Code);
                break;
            case Commands.OS osCommand:
                if (osCommand.Command == Commands.OS.Type.SetTitle)
                {
                    this.Title = osCommand.Title;
                    this.OnPropertyChanged("Title");
                }
                break;
            case Commands.ControlSequence csiCommand:
                this.HandleControlSequence(csiCommand);
                break;
            case Commands.Unsupported unsupported:
                break;
            default:
                throw new InvalidOperationException($"Unknown command type passed: {this.parser.Command?.GetType()}.");
            }
        }

        private void HandleControlCharacter(Commands.ControlCharacter.ControlCode code)
        {
            switch (code)
            {
            case Commands.ControlCharacter.ControlCode.NUL:
                // XXX: do we want to print these in some magic way? it seems like most terminals just discard these characters when received.
                break;
            case Commands.ControlCharacter.ControlCode.BEL:
                // XXX: need to raise a beep event.
                break;
            case Commands.ControlCharacter.ControlCode.BS:
                // backspace wrap to previous line if not on first line, if we're at 0,0 we go nowhere.
                if (this.cursorX == 0)
                {
                    if (this.cursorY > 0)
                    {
                        --this.cursorY;
                        this.cursorX = (short)(this.Width - 1);
                    }
                }
                else
                {
                    --this.cursorX;
                }
                break;
            case Commands.ControlCharacter.ControlCode.CR:
                this.cursorX = 0;
                break;
            case Commands.ControlCharacter.ControlCode.FF: // NB: could clear screen with this if we were so inclined. apparently xterm treats this as LF though, let's emulate.
            case Commands.ControlCharacter.ControlCode.LF:
                if (this.CurrentLine == this.bottomVisibleLine)
                {
                    this.ScrollDown();
                }

                this.cursorY = (short)Math.Min(this.Height - 1, this.cursorY + 1);
                break;
            case Commands.ControlCharacter.ControlCode.TAB:
                // XXX: we don't handle commands to set tab stops yet but I guess need to do so at some point!
                this.cursorX = (short)Math.Max(this.Width - 1, (this.cursorX + 8 - (this.cursorX % 8)));
                break;
            default:
                // XXX: should log the sequence.
                Logger.Verbose("Encountered unsupported sequence.");
                break;
            }
        }

        private void HandleControlSequence(Commands.ControlSequence cmd)
        {
            switch (cmd)
            {
            case Commands.CursorMove cu:
                this.HandleCursorMove(cu);
                break;
            case Commands.EraseCharacter ech:
                this.HandleEraseCharacter(ech);
                break;
            case Commands.EraseIn eid when eid.Type == Commands.EraseIn.EraseType.Display:
                this.HandleEraseInDisplay(eid);
                break;
            case Commands.EraseIn eil when eil.Type == Commands.EraseIn.EraseType.Line:
                this.HandleEraseInLine(eil);
                break;
            case Commands.SetCursorPosition scp:
                this.HandleSetCursorPosition(scp);
                break;
            case Commands.SetMode sm:
                this.HandleSetMode(sm);
                break;
            default:
                throw new InvalidOperationException($"Unknown CSI command type {cmd.GetType()}.");
            }
        }

        private void HandleCursorMove(Commands.CursorMove cu)
        {
            switch (cu.Direction)
            {
            case Commands.CursorMove.CursorDirection.Up:
                this.cursorY = (short)Math.Max(0, this.cursorY - cu.Count);
                break;
            case Commands.CursorMove.CursorDirection.Down:
                this.cursorY = (short)Math.Min(this.Height - 1, this.cursorY + cu.Count);
                break;
            case Commands.CursorMove.CursorDirection.Backward:
                this.cursorX = (short)Math.Max(0, this.cursorX - cu.Count);
                break;
            case Commands.CursorMove.CursorDirection.Forward:
                this.cursorX = (short)Math.Min(this.Width - 1, this.cursorX + cu.Count);
                break;
            }
        }

        private void HandleEraseCharacter(Commands.EraseCharacter ech)
        {
            // erase characters starting at the current cursor position and possibly advancing down lines. do not erase below the bottom visible line.
            var y = this.CurrentLine;
            var x = this.cursorX;
            for (var c = 0; c < ech.Count; ++c)
            {
                this.lines[y].SetGlyph(x, 0x20);
                ++x;
                if (x == this.Width)
                {
                    if (++y > this.bottomVisibleLine)
                    {
                        break;
                    }
                    x = 0;
                }
            }
        }

        private void HandleEraseInDisplay(Commands.EraseIn eid)
        {
            int startY, endY;
            switch (eid.Direction)
            {
            case Commands.EraseIn.Parameter.All:
                startY = this.topVisibleLine;
                endY = this.bottomVisibleLine;
                break;
            case Commands.EraseIn.Parameter.Before:
                startY = this.topVisibleLine;
                endY = this.CurrentLine;
                break;
            case Commands.EraseIn.Parameter.After:
                startY = this.CurrentLine;
                endY = this.bottomVisibleLine;
                break;
            default:
                return;
            }

            for (var y = startY; y <= endY; ++y)
            {
                this.lines[y].Clear();
            }
        }

        private void HandleEraseInLine(Commands.EraseIn eil)
        {
            int startX, endX;
            switch (eil.Direction)
            {
            case Commands.EraseIn.Parameter.All:
                startX = 0;
                endX = this.Width - 1;
                break;
            case Commands.EraseIn.Parameter.Before:
                startX = 0;
                endX = this.cursorX;
                break;
            case Commands.EraseIn.Parameter.After:
                startX = this.cursorX;
                endX = this.Width - 1;
                break;
            default:
                return;
            }

            for (var x = startX; x <= endX; ++x)
            {
                this.lines[this.CurrentLine].SetGlyph(x, 0x20);
            }
        }

        private void HandleSetCursorPosition(Commands.SetCursorPosition scp)
        {
            if (scp.PosX > -1)
            {
                this.cursorX = (short)Math.Min(this.Width - 1, scp.PosX);
            }
            if (scp.PosY > -1)
            {
                this.cursorY = (short)Math.Min(this.Height - 1, scp.PosY);
            }
        }

        private void HandleSetMode(Commands.SetMode sm)
        {
            switch (sm.Setting)
            {
            case Commands.SetMode.Parameter.CursorBlink:
                this.CursorBlink = sm.Set;
                break;
            case Commands.SetMode.Parameter.CursorShow:
                this.CursorVisible = sm.Set;
                break;
            }
        }

        /// <summary>
        /// Scroll the visible buffer down, adding new lines if needed.
        /// If we're at the bottom of the buffer we will replace lines from the top with new, blank lines.
        /// </summary>
        private void ScrollDown(int lines = 1)
        {
            while (lines > 0)
            {
                --lines;
                if (this.bottomVisibleLine == this.lines.Capacity - 1)
                {
                    this.lines.PushBack(new Line(this.lines[this.bottomVisibleLine])); // will force an old line from the buffer;
                }
                else
                {
                    ++this.topVisibleLine;
                    ++this.bottomVisibleLine;
                    if (this.lines.Size <= this.bottomVisibleLine)
                    {
                        this.lines.PushBack(new Line(this.lines[this.bottomVisibleLine - 1]));
                    }
                }
            }
        }

        /// <summary>
        /// Render character-by-character onto the specified target.
        /// </summary>
        public void Render(IRenderTarget target)
        {
            lock (this.renderLock)
            {
                for (var y = 0; y < this.Height; ++y)
                {
                    var renderLine = this.topVisibleLine + y;
                    var line = this.lines[renderLine];

                    for (var x = 0; x < this.Width; ++x)
                    { 
                        target.RenderCharacter(line.Get(x), x, y);
                    }
                }
            }
        }

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        #endregion
    }
}
