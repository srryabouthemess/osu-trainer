using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NAudio.Wave;
using NAudio.Lame;
using NAudio.Vorbis;

namespace osu_trainer
{
    internal class SongSpeedChanger
    {
        public static void GenerateAudioFile(string inFile, string outFile, decimal effectiveMultiplier, bool changePitch = false, bool preDT = false, bool highQuality = false)
        {
            decimal DTCompensatedMultiplier = effectiveMultiplier / 1.5M;

            string ext = Path.GetExtension(inFile);
            string temp1 = Path.Combine(Guid.NewGuid().ToString() + ext); // audio copy
            string temp2 = Path.Combine(Guid.NewGuid().ToString() + ".wav"); // decoded wav
            string temp3 = Path.Combine(Guid.NewGuid().ToString() + ".wav"); // stretched file

            File.Copy(inFile, temp1);

            // mp3/ogg => wav
            if (ext.ToLower() == ".mp3")
            {
                using (Mp3FileReader mp3 = new Mp3FileReader(temp1))
                using (WaveStream wav = WaveFormatConversionStream.CreatePcmStream(mp3))
                {
                    // NAudio doesn't trim the encoder/decoder delay, but osu! does (gapless LAME tag).
                    // Left in, that silence gets stretched and the new audio ends up late.
                    int delaySamples = GetGaplessDelaySamples(temp1);
                    if (delaySamples > 0)
                    {
                        long skip = (long)delaySamples * wav.WaveFormat.BlockAlign;
                        wav.Position = Math.Min(skip, wav.Length);
                    }
                    WaveFileWriter.CreateWaveFile(temp2, wav);
                }
            }
            else if (ext.ToLower() == ".ogg")
            {
                using (VorbisWaveReader vorbis = new VorbisWaveReader(temp1))
                    WaveFileWriter.CreateWaveFile(temp2, vorbis.ToWaveProvider16());
            }
            else
            {
                throw new Exception($"audio file not supported: {ext}");
            }


            // stretch (or speed up) wav
            string quick = highQuality ? "" : "-quick";
            string naa = highQuality ? "" : "-naa";

            decimal multiplier = preDT ? DTCompensatedMultiplier : effectiveMultiplier;
            // invariant culture: this runs on a background thread, and soundstretch stops parsing at a "," decimal separator
            string tempo = string.Format(CultureInfo.InvariantCulture, "-tempo={0}", (multiplier - 1) * 100);

            decimal cents = (decimal)(1200.0 * Math.Log((double)effectiveMultiplier) / Math.Log(2));
            decimal semitones = cents / 100.0M;
            string pitch = changePitch ? string.Format(CultureInfo.InvariantCulture, "-pitch={0}", semitones) : "";

            string options = $"{quick} {naa} {tempo} {pitch}";
            RunSoundStretch($"\"{temp2}\" \"{temp3}\" {options}");


            // wav => mp3
            if (File.Exists(outFile))
                File.Delete(outFile);
            using (var wav = new WaveFileReader(temp3))
            using (var mp3 = new LameMP3FileWriter(outFile, wav.WaveFormat, highQuality ? LAMEPreset.STANDARD : LAMEPreset.MEDIUM))
            {
                // soundstretch shifts the audio by a few dozen ms depending on the tempo (early when slowing down),
                // so measure it with the same options and undo it
                long lag = (long)MeasureStretchLagSamples(wav.WaveFormat, options, (double)multiplier) * wav.WaveFormat.BlockAlign;
                if (lag < 0)
                    mp3.Write(new byte[-lag], 0, (int)-lag);
                else if (lag > 0)
                    wav.Position = Math.Min(lag, wav.Length);
                wav.CopyTo(mp3);
            }


            // Clean up
            try
            {
                File.Delete(temp1);
                File.Delete(temp2);
                File.Delete(temp3);
            }
            catch { } // don't shit the bed if we can't delete temp files
        }

        private static void RunSoundStretch(string arguments)
        {
            Process soundstretch = new Process();
            soundstretch.StartInfo.FileName = Path.Combine("binaries", "soundstretch.exe");
            soundstretch.StartInfo.Arguments = arguments;
            Console.WriteLine(soundstretch.StartInfo.Arguments);
            soundstretch.StartInfo.UseShellExecute = false;
            soundstretch.StartInfo.CreateNoWindow = true;
            soundstretch.Start();
            soundstretch.WaitForExit();
        }

        // stretches a click track with the same options and returns how far the clicks land from where they should
        // (in samples, negative = early)
        private static int MeasureStretchLagSamples(WaveFormat format, string options, double tempo)
        {
            const int clicks = 20, clickMs = 60, maxLagMs = 200;
            int sampleRate = format.SampleRate, channels = format.Channels;
            string calibIn = Path.Combine(Guid.NewGuid().ToString() + ".wav");
            string calibOut = Path.Combine(Guid.NewGuid().ToString() + ".wav");
            try
            {
                // click (1 kHz tone) at 500ms, 1500ms, 2500ms...
                using (var writer = new WaveFileWriter(calibIn, new WaveFormat(sampleRate, 16, channels)))
                {
                    int total = (clicks + 1) * sampleRate;
                    for (int i = 0; i < total; i++)
                    {
                        double t = (double)i / sampleRate;
                        double inClick = (t - 0.5) % 1.0;
                        short value = t >= 0.5 && inClick < clickMs / 1000.0 ? (short)(16000 * Math.Sin(2 * Math.PI * 1000 * t)) : (short)0;
                        for (int c = 0; c < channels; c++)
                            writer.WriteSample(value / 32768f);
                    }
                }

                RunSoundStretch($"\"{calibIn}\" \"{calibOut}\" {options}");

                // loudness per millisecond
                float[] envelope;
                using (var reader = new WaveFileReader(calibOut))
                {
                    var samples = reader.ToSampleProvider();
                    int outChannels = reader.WaveFormat.Channels;
                    long frames = reader.SampleCount;
                    envelope = new float[frames * 1000 / sampleRate + 1];
                    float[] buffer = new float[outChannels * 4096];
                    long frame = 0;
                    int read;
                    while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
                        for (int i = 0; i < read; i += outChannels, frame++)
                            envelope[frame * 1000 / sampleRate] += Math.Abs(buffer[i]);
                }

                // where the clicks should be after stretching
                bool[] expected = new bool[envelope.Length];
                for (int k = 0; k < clicks; k++)
                {
                    int start = (int)((500 + 1000 * k) / tempo);
                    int end = (int)((500 + 1000 * k + clickMs) / tempo);
                    for (int ms = start; ms < end && ms < expected.Length; ms++)
                        expected[ms] = true;
                }

                int bestLag = 0;
                double bestScore = double.MinValue;
                for (int lag = -maxLagMs; lag <= maxLagMs; lag++)
                {
                    double score = 0;
                    for (int ms = maxLagMs; ms < envelope.Length - maxLagMs; ms++)
                        if (expected[ms])
                            score += envelope[ms + lag];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestLag = lag;
                    }
                }
                return bestLag * sampleRate / 1000;
            }
            catch (Exception e)
            {
                Console.WriteLine(e); // worst case the audio is a bit off, same as before
                return 0;
            }
            finally
            {
                try
                {
                    File.Delete(calibIn);
                    File.Delete(calibOut);
                }
                catch { }
            }
        }

        // encoder delay from the LAME tag + 529 samples of mp3 decoder delay, or 0 if there's no tag
        private static int GetGaplessDelaySamples(string mp3File)
        {
            const int decoderDelay = 529;
            byte[] data = new byte[16384];
            int length;
            using (var fs = File.OpenRead(mp3File))
            {
                Id3v2Tag.ReadTag(fs); // skips the id3 tag if there is one
                length = fs.Read(data, 0, data.Length);
            }

            int xing = -1;
            for (int i = 0; i < 256 && i + 8 <= length; i++) // the tag lives in the first frame
            {
                if ((data[i] == 'X' && data[i + 1] == 'i' && data[i + 2] == 'n' && data[i + 3] == 'g') ||
                    (data[i] == 'I' && data[i + 1] == 'n' && data[i + 2] == 'f' && data[i + 3] == 'o'))
                {
                    xing = i;
                    break;
                }
            }
            if (xing < 0)
                return 0;

            int flags = (data[xing + 4] << 24) | (data[xing + 5] << 16) | (data[xing + 6] << 8) | data[xing + 7];
            int lame = xing + 8;
            if ((flags & 1) != 0) lame += 4;   // frames
            if ((flags & 2) != 0) lame += 4;   // bytes
            if ((flags & 4) != 0) lame += 100; // toc
            if ((flags & 8) != 0) lame += 4;   // quality
            if (lame + 24 > length)
                return 0;

            int encoderDelay = (data[lame + 21] << 4) | (data[lame + 22] >> 4);
            return encoderDelay + decoderDelay;
        }
    }
}