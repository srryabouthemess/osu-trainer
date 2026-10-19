using System;
using System.Diagnostics;
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
            string tempo = $"-tempo={(multiplier - 1) * 100}";

            decimal cents = (decimal)(1200.0 * Math.Log((double)effectiveMultiplier) / Math.Log(2));
            decimal semitones = cents / 100.0M;
            string pitch = changePitch ? $"-pitch={semitones}" : "";

            Process soundstretch = new Process();
            soundstretch.StartInfo.FileName = Path.Combine("binaries", "soundstretch.exe");
            soundstretch.StartInfo.Arguments = $"\"{temp2}\" \"{temp3}\" {quick} {naa} {tempo} {pitch}";
            Console.WriteLine(soundstretch.StartInfo.Arguments);
            soundstretch.StartInfo.UseShellExecute = false;
            soundstretch.StartInfo.CreateNoWindow = true;
            soundstretch.Start();
            soundstretch.WaitForExit();


            // wav => mp3
            if (File.Exists(outFile))
                File.Delete(outFile);
            using (var wav = new WaveFileReader(temp3))
            using (var mp3 = new LameMP3FileWriter(outFile, wav.WaveFormat, highQuality ? LAMEPreset.STANDARD : LAMEPreset.MEDIUM))
                wav.CopyTo(mp3);


            // Clean up
            try
            {
                File.Delete(temp1);
                File.Delete(temp2);
                File.Delete(temp3);
            }
            catch { } // don't shit the bed if we can't delete temp files
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