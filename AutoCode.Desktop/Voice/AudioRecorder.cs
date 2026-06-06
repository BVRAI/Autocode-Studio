using System.IO;
using NAudio.Wave;

namespace AutoCode.Desktop.Voice;

/// <summary>
/// Captures microphone audio to an in-memory 16 kHz / 16-bit / mono WAV via NAudio.
/// Capture runs on NAudio's own callback thread; callers only touch <see cref="Start"/>/<see cref="StopAsync"/>.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _stream;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<byte[]>? _stopTcs;
    private bool _disposed;

    public bool IsRecording { get; private set; }

    public static bool HasInputDevice => WaveInEvent.DeviceCount > 0;

    /// <summary>Optional RMS level (0..1) per buffer, for a recording indicator.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Begin capturing. Throws <see cref="InvalidOperationException"/> if no input device is present.</summary>
    public void Start()
    {
        if (IsRecording)
        {
            return;
        }

        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("No microphone detected.");
        }

        var format = new WaveFormat(16000, 16, 1);
        _stream = new MemoryStream();
        _writer = new WaveFileWriter(_stream, format);
        _waveIn = new WaveInEvent { WaveFormat = format, BufferMilliseconds = 50 };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        IsRecording = true;
    }

    /// <summary>Stop capturing and return the finalized WAV bytes (header sizes written). Empty if nothing was captured.</summary>
    public Task<byte[]> StopAsync()
    {
        if (!IsRecording || _waveIn is null)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        IsRecording = false;
        _stopTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waveIn.StopRecording(); // RecordingStopped fires asynchronously after the final buffer
        return _stopTcs.Task;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        writer.Write(e.Buffer, 0, e.BytesRecorded);

        var level = LevelChanged;
        if (level is null || e.BytesRecorded < 2)
        {
            return;
        }

        long sumSquares = 0;
        var samples = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            sumSquares += (long)sample * sample;
            samples++;
        }

        if (samples > 0)
        {
            var rms = Math.Sqrt(sumSquares / (double)samples) / short.MaxValue;
            level.Invoke((float)Math.Clamp(rms, 0, 1));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        try
        {
            // Disposing the writer finalizes the RIFF/data chunk sizes in the WAV header.
            // MemoryStream.ToArray() still works after the stream is closed, so we read after dispose.
            _writer?.Dispose();
            _writer = null;
            var bytes = _stream?.ToArray() ?? Array.Empty<byte>();
            if (e.Exception is not null)
            {
                _stopTcs?.TrySetException(e.Exception);
            }
            else
            {
                _stopTcs?.TrySetResult(bytes);
            }
        }
        catch (Exception ex)
        {
            _stopTcs?.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_waveIn is not null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                if (IsRecording)
                {
                    try { _waveIn.StopRecording(); } catch { /* ignore */ }
                }

                _waveIn.Dispose();
            }
        }
        catch { /* ignore */ }

        _waveIn = null;
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _stream = null;
        IsRecording = false;
        _stopTcs?.TrySetResult(Array.Empty<byte>());
    }
}
