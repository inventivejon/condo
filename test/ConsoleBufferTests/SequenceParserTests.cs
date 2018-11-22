namespace ConsoleBufferTests
{
    using ConsoleBuffer;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class SequenceParserTests
    {
        [TestMethod]
        public void Basic()
        {
            var parser = new SequenceParser();
            foreach (var c in "hello world")
            {
                Assert.AreEqual(ParserAppendResult.Render, parser.Append(c));
            }
        }

        [TestMethod]
        [DataRow('\0', ConsoleBuffer.Commands.ControlCharacter.ControlCode.NUL)]
        [DataRow('\a', ConsoleBuffer.Commands.ControlCharacter.ControlCode.BEL)]
        [DataRow('\b', ConsoleBuffer.Commands.ControlCharacter.ControlCode.BS)]
        [DataRow('\f', ConsoleBuffer.Commands.ControlCharacter.ControlCode.FF)]
        [DataRow('\n', ConsoleBuffer.Commands.ControlCharacter.ControlCode.LF)]
        [DataRow('\r', ConsoleBuffer.Commands.ControlCharacter.ControlCode.CR)]
        [DataRow('\t', ConsoleBuffer.Commands.ControlCharacter.ControlCode.TAB)]
        [DataRow('\v', ConsoleBuffer.Commands.ControlCharacter.ControlCode.LF)] // lmao vertical tabs
        public void ControlCharacters(char c, ConsoleBuffer.Commands.ControlCharacter.ControlCode code)
        {
            var parser = new SequenceParser();
            Assert.AreEqual(ParserAppendResult.Complete, parser.Append(c));
            Assert.IsInstanceOfType(parser.Command, typeof(ConsoleBuffer.Commands.ControlCharacter));
            Assert.AreEqual(code, (parser.Command as ConsoleBuffer.Commands.ControlCharacter).Code);
        }

        [TestMethod]
        [DataRow('^')]
        [DataRow('_')]
        public void UnsupportedAncientStuff(char c)
        {
            var ancientCommand = $"\x1b{c} here is a long string of nonsense.\0";

            var parser = new SequenceParser();
            for (var i = 0; i < ancientCommand.Length - 1; ++i)
            {
                Assert.AreEqual(ParserAppendResult.Pending, parser.Append(ancientCommand[i]));
            }
            Assert.AreEqual(ParserAppendResult.Complete, parser.Append(ancientCommand[ancientCommand.Length - 1]));
            Assert.IsInstanceOfType(parser.Command, typeof(ConsoleBuffer.Commands.Unsupported));
        }

        [TestMethod]
        public void OSCommands()
        {
            const string title = "this is a random title";
            var command = $"\x1b]2;{title}\a";

            var parser = new SequenceParser();
            for (var i = 0; i < command.Length - 1; ++i)
            {
                Assert.AreEqual(ParserAppendResult.Pending, parser.Append(command[i]));
            }
            Assert.AreEqual(ParserAppendResult.Complete, parser.Append(command[command.Length - 1]));
            Assert.IsInstanceOfType(parser.Command, typeof(ConsoleBuffer.Commands.OS));

            var osCmd = parser.Command as ConsoleBuffer.Commands.OS;
            Assert.AreEqual(title, osCmd.Title);
        }
    }
}