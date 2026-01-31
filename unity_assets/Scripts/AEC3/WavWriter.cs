using System;
using System.IO;
using System.Text;

public class WavWriter
{
    private FileStream _fileStream;
    private BinaryWriter _writer;
    private int _sampleRate;
    private int _paramChannels;
    private int _byteRate;
    private int _blockAlign;

    public WavWriter(string path, int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _paramChannels = channels;
        _fileStream = new FileStream(path, FileMode.Create);
        _writer = new BinaryWriter(_fileStream);
        
        // Write placeholder header
        WriteWavHeader(0); 
    }

    public void WriteSamples(short[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            _writer.Write(samples[i]);
        }
    }

    public void Close()
    {
        if (_writer == null) return;
        
        long length = _fileStream.Length - 44; // Data size
        _fileStream.Seek(0, SeekOrigin.Begin);
        WriteWavHeader((int)length);
        _writer.Close();
        _fileStream.Close();
        _writer = null;
    }

    private void WriteWavHeader(int dataLength)
    {
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write(dataLength + 36);
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16); // Subchunk1Size (16 for PCM)
        _writer.Write((short)1); // AudioFormat (1 for PCM)
        _writer.Write((short)_paramChannels);
        _writer.Write(_sampleRate);
        _writer.Write(_sampleRate * _paramChannels * 2); // ByteRate
        _writer.Write((short)(_paramChannels * 2)); // BlockAlign
        _writer.Write((short)16); // BitsPerSample
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write(dataLength);
    }
}
