using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Nomi
{
    // Text recognition with the OCR engine built into Windows 10/11 (offline, uses the
    // languages installed on the machine).
    static class ScreenOcr
    {
        // Kept apart so that a machine without the WinRT OCR types fails here, catchably,
        // rather than when the caller is compiled by the JIT.
        public static async Task<string> RecognizeAsync(Bitmap image)
        {
            try { return await RecognizeCore(image); }
            catch (TypeLoadException e) { throw new InvalidOperationException("La reconnaissance de texte de Windows n'est pas disponible.", e); }
            catch (FileNotFoundException e) { throw new InvalidOperationException("La reconnaissance de texte de Windows n'est pas disponible.", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task<string> RecognizeCore(Bitmap image)
        {
            OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                foreach (string tag in new[] { "fr-FR", "en-US", "en-GB" })
                {
                    var lang = new Windows.Globalization.Language(tag);
                    if (OcrEngine.IsLanguageSupported(lang)) { engine = OcrEngine.TryCreateFromLanguage(lang); break; }
                }
            }
            if (engine == null)
                throw new InvalidOperationException("Aucune langue de reconnaissance de texte n'est installée dans Windows.");

            byte[] png;
            using (Bitmap prepared = Prepare(image, (int)OcrEngine.MaxImageDimension))
            using (var ms = new MemoryStream())
            {
                prepared.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }

            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(png);
                    await Wait(writer.StoreAsync());
                    await Wait(writer.FlushAsync());
                    writer.DetachStream();
                }
                stream.Seek(0);
                BitmapDecoder decoder = await Wait(BitmapDecoder.CreateAsync(stream));
                using (SoftwareBitmap bitmap = await Wait(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)))
                {
                    OcrResult result = await Wait(engine.RecognizeAsync(bitmap));
                    var sb = new StringBuilder();
                    foreach (OcrLine line in result.Lines) sb.AppendLine(line.Text);
                    return sb.ToString().Trim();
                }
            }
        }

        // The await helpers of System.Runtime.WindowsRuntime do not bind against the per-namespace
        // metadata shipped in System32\WinMetadata, so completion is bridged by hand.
        static Task<T> Wait<T>(IAsyncOperation<T> operation)
        {
            var tcs = new TaskCompletionSource<T>();
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed) tcs.TrySetResult(op.GetResults());
                    else if (status == AsyncStatus.Canceled) tcs.TrySetCanceled();
                    else tcs.TrySetException(op.ErrorCode ?? new InvalidOperationException("Opération WinRT en échec"));
                }
                catch (Exception e) { tcs.TrySetException(e); }
            };
            return tcs.Task;
        }

        // Small screen text is read much better once enlarged; the engine also has a size limit.
        static Bitmap Prepare(Bitmap source, int maxDimension)
        {
            if (maxDimension <= 0) maxDimension = 2600;
            double scale = source.Height < 900 && source.Width < 1300 ? 2.0 : 1.0;
            scale = Math.Min(scale, maxDimension / (double)Math.Max(source.Width, source.Height));
            int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
            var target = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(target))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, w, h);
            }
            return target;
        }
    }
}
