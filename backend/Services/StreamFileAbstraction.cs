using System;
using System.IO;

namespace WebMusic.Backend.Services
{
    public class StreamFileAbstraction : TagLib.File.IFileAbstraction
    {
        private readonly Stream _stream;
        private readonly string _name;

        public StreamFileAbstraction(string name, Stream stream)
        {
            _name = name;
            _stream = stream;
        }

        public string Name => _name;

        public Stream ReadStream => _stream;

        public Stream WriteStream => throw new NotImplementedException();

        public void CloseStream(Stream stream)
        {
            // Do not close the underlying stream here, let the caller manage it.
        }
    }
}
