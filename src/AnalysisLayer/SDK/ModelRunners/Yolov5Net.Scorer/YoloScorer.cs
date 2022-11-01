﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

using Yolov5Net.Scorer.Extensions;
using Yolov5Net.Scorer.Models.Abstract;

namespace Yolov5Net.Scorer
{
    /// <summary>
    /// Yolov5 scorer.
    /// </summary>
    public class YoloScorer<T> : IDisposable where T : YoloModel
    {
        private static object _lock = new object();
        private readonly T _model;

        private readonly InferenceSession _inferenceSession;

        /// <summary>
        /// Outputs value between 0 and 1.
        /// </summary>
        private float Sigmoid(float value)
        {
            return 1 / (1 + (float)Math.Exp(-value));
        }

        /// <summary>
        /// Converts xywh bbox format to xyxy.
        /// </summary>
        private float[] Xywh2xyxy(float[] source)
        {
            var result = new float[4];

            result[0] = source[0] - source[2] / 2f;
            result[1] = source[1] - source[3] / 2f;
            result[2] = source[0] + source[2] / 2f;
            result[3] = source[1] + source[3] / 2f;

            return result;
        }

        /// <summary>
        /// Returns value clamped to the inclusive range of min and max.
        /// </summary>
        public float Clamp(float value, float min, float max)
        {
            return (value < min) ? min : (value > max) ? max : value;
        }

        /// <summary>
        /// Resizes image keeping ratio to fit model input size. Make sure to dispose of the returned
        /// image!
        /// </summary>
        private SKImage ResizeImage(SKImage image)
        {
            var (w, h) = (image.Width, image.Height); // image width and height
            var (xRatio, yRatio) = (_model.Width / (float)w, _model.Height / (float)h); // x, y ratios
            var ratio = Math.Min(xRatio, yRatio); // ratio = resized / original
            var (width, height) = ((int)(w * ratio), (int)(h * ratio)); // roi width and height
            var (x, y) = ((_model.Width / 2) - (width / 2), (_model.Height / 2) - (height / 2)); // roi x and y coordinates

            // SKImage version
            var destRect = new SKRectI(x, y, x + width, y + height); // region of interest
            var imageInfo = new SKImageInfo(_model.Width, _model.Height, image.ColorType, image.AlphaType);
            using var surface = SKSurface.Create(imageInfo);
            using var paint = new SKPaint();

            paint.IsAntialias   = true;
            paint.FilterQuality = SKFilterQuality.High;

            surface.Canvas.DrawImage(image, destRect, paint);
            surface.Canvas.Flush();

            return surface.Snapshot();

            /* System.Drawing version
            var regionToDraw = new Rectangle(x, y, width, height); // region of interest
            Bitmap output    = new Bitmap(_model.Width, _model.Height, image.PixelFormat);
            using var graphics = Graphics.FromImage(output);

            graphics.Clear(Color.FromArgb(0, 0, 0, 0)); // clear canvas

            graphics.SmoothingMode     = SmoothingMode.None;         // no smoothing
            graphics.InterpolationMode = InterpolationMode.Bilinear; // bilinear interpolation
            graphics.PixelOffsetMode   = PixelOffsetMode.Half;       // half pixel offset

            graphics.DrawImage(image, regionToDraw); // draw scaled

            return output;
            */
        }

        /// <summary>
        /// Extracts pixels into tensor for net input.
        /// </summary>
        private Tensor<float> ExtractPixels(SKImage image)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, image.Height, image.Width });

            var    bitmap        = SKBitmap.FromImage(image);
            IntPtr pixelsAddr    = bitmap.GetPixels();
            int    bytesPerPixel = 4;
            int    stride        = bytesPerPixel * image.Width;

            unsafe // speed up conversion by direct work with memory
            {
                byte* ptr = (byte*)pixelsAddr.ToPointer();

                Parallel.For(0, image.Height, (y) =>
                {
                    byte* row = ptr + (y * stride);

                    Parallel.For(0, image.Width, (x) =>
                    {
                        // alpha           = row[x * bytesPerPixel + 3] / 255.0F; // a 
                        tensor[0, 0, y, x] = row[x * bytesPerPixel + 2] / 255.0F; // r
                        tensor[0, 1, y, x] = row[x * bytesPerPixel + 1] / 255.0F; // g
                        tensor[0, 2, y, x] = row[x * bytesPerPixel + 0] / 255.0F; // b
                    });
                });
            }

            /*
            Bitmap bitmap = image.ToBitmap();

            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, bitmap.PixelFormat);
            int bytesPerPixel = Bitmap.GetPixelFormatSize(bitmap.PixelFormat) / 8;
            int stride        = bitmapData.Stride;

            unsafe // speed up conversion by direct work with memory
            {
                byte* ptr = (byte*)bitmapData.Scan0;

                Parallel.For(0, bitmap.Height, (y) =>
                {
                    byte* row = ptr + (y * stride);

                    Parallel.For(0, bitmap.Width, (x) =>
                    {
                        // alpha           = row[x * bytesPerPixel + 3] / 255.0F; // a 
                        tensor[0, 0, y, x] = row[x * bytesPerPixel + 2] / 255.0F; // r
                        tensor[0, 1, y, x] = row[x * bytesPerPixel + 1] / 255.0F; // g
                        tensor[0, 2, y, x] = row[x * bytesPerPixel + 0] / 255.0F; // b
                    });
                });

                bitmap.UnlockBits(bitmapData);
            }
            */

            return tensor;
        }

        /// <summary>
        /// Runs inference session.
        /// </summary>
        /// <param name="image">The input image</param>
        /// <returns>A dense tensor containing the image pixels</returns>
        private DenseTensor<float>[] Inference(SKImage image)
        {
            SKImage resized = null;

            if (image.Width != _model.Width || image.Height != _model.Height)
            {
                resized = ResizeImage(image); // fit image size to specified input size
            }

            var inputs = new List<NamedOnnxValue> // add image as onnx input
            {
                NamedOnnxValue.CreateFromTensor("images", ExtractPixels(resized ?? image))
            };

            if (resized != null)
                resized.Dispose();

            var output = new List<DenseTensor<float>>();

            lock (_lock)
            {
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = _inferenceSession.Run(inputs); // run inference

                foreach (var item in _model.Outputs) // add outputs for processing
                {
                    output.Add(result.First(x => x.Name == item).Value as DenseTensor<float>);
                };
            return output.ToArray();
            }
        }

        /// <summary>
        /// Parses net output (detect) to predictions.
        /// </summary>
        /// <param name="output">The first output from an inference call</param>
        /// <param name="image">The original input image</param>
        /// <returns>A list of Yolo predictions</returns>
        private List<YoloPrediction> ParseDetect(DenseTensor<float> output, SKImage image)
        {
            var result = new ConcurrentBag<YoloPrediction>();

            var (w, h) = (image.Width, image.Height); // image w and h
            var (xGain, yGain) = (_model.Width / (float)w, _model.Height / (float)h); // x, y gains
            var gain = Math.Min(xGain, yGain); // gain = resized / original

            var (xPad, yPad) = ((_model.Width - w * gain) / 2, (_model.Height - h * gain) / 2); // left, right pads

            Parallel.For(0, (int)output.Length / _model.Dimensions, (i) =>
            {
                if (output[0, i, 4] <= _model.Confidence) return; // skip low obj_conf results

                Parallel.For(5, _model.Dimensions, (j) =>
                {
                    output[0, i, j] = output[0, i, j] * output[0, i, 4]; // mul_conf = obj_conf * cls_conf
                });

                Parallel.For(5, _model.Dimensions, (k) =>
                {
                    if (output[0, i, k] <= _model.MulConfidence) return; // skip low mul_conf results

                    float xMin = ((output[0, i, 0] - output[0, i, 2] / 2) - xPad) / gain; // unpad bbox tlx to original
                    float yMin = ((output[0, i, 1] - output[0, i, 3] / 2) - yPad) / gain; // unpad bbox tly to original
                    float xMax = ((output[0, i, 0] + output[0, i, 2] / 2) - xPad) / gain; // unpad bbox brx to original
                    float yMax = ((output[0, i, 1] + output[0, i, 3] / 2) - yPad) / gain; // unpad bbox bry to original

                    xMin = Clamp(xMin, 0, w - 0); // clip bbox tlx to boundaries
                    yMin = Clamp(yMin, 0, h - 0); // clip bbox tly to boundaries
                    xMax = Clamp(xMax, 0, w - 1); // clip bbox brx to boundaries
                    yMax = Clamp(yMax, 0, h - 1); // clip bbox bry to boundaries

                    YoloLabel label = _model.Labels[k - 5];

                    var prediction = new YoloPrediction(label, output[0, i, k])
                    {
                        Rectangle = new SKRect(xMin, yMin, xMax, yMax)
                    };

                    result.Add(prediction);
                });
            });

            return result.ToList();
        }

        /// <summary>
        /// Parses net outputs (sigmoid) to predictions.
        /// </summary>
        /// <param name="output">All outputs from an inference call</param>
        /// <param name="image">The original input image</param>
        /// <returns>A list of Yolo predictions</returns>
        private List<YoloPrediction> ParseSigmoid(DenseTensor<float>[] output, SKImage image)
        {
            var result = new ConcurrentBag<YoloPrediction>();

            var (w, h) = (image.Width, image.Height); // image w and h
            var (xGain, yGain) = (_model.Width / (float)w, _model.Height / (float)h); // x, y gains
            var gain = Math.Min(xGain, yGain); // gain = resized / original

            var (xPad, yPad) = ((_model.Width - w * gain) / 2, (_model.Height - h * gain) / 2); // left, right pads

            Parallel.For(0, output.Length, (i) => // iterate model outputs
            {
                int shapes = _model.Shapes[i]; // shapes per output

                Parallel.For(0, _model.Anchors[0].Length, (a) => // iterate anchors
                {
                    Parallel.For(0, shapes, (y) => // iterate shapes (rows)
                    {
                        Parallel.For(0, shapes, (x) => // iterate shapes (columns)
                        {
                            int offset = (shapes * shapes * a + shapes * y + x) * _model.Dimensions;

                            float[] buffer = output[i].Skip(offset).Take(_model.Dimensions).Select(Sigmoid).ToArray();

                            if (buffer[4] <= _model.Confidence) return; // skip low obj_conf results

                            List<float> scores = buffer.Skip(5).Select(b => b * buffer[4]).ToList(); // mul_conf = obj_conf * cls_conf

                            float mulConfidence = scores.Max(); // max confidence score

                            if (mulConfidence <= _model.MulConfidence) return; // skip low mul_conf results

                            float rawX = (buffer[0] * 2 - 0.5f + x) * _model.Strides[i]; // predicted bbox x (center)
                            float rawY = (buffer[1] * 2 - 0.5f + y) * _model.Strides[i]; // predicted bbox y (center)

                            float rawW = (float)Math.Pow(buffer[2] * 2, 2) * _model.Anchors[i][a][0]; // predicted bbox w
                            float rawH = (float)Math.Pow(buffer[3] * 2, 2) * _model.Anchors[i][a][1]; // predicted bbox h

                            float[] xyxy = Xywh2xyxy(new float[] { rawX, rawY, rawW, rawH });

                            float xMin = Clamp((xyxy[0] - xPad) / gain, 0, w - 0); // unpad, clip tlx
                            float yMin = Clamp((xyxy[1] - yPad) / gain, 0, h - 0); // unpad, clip tly
                            float xMax = Clamp((xyxy[2] - xPad) / gain, 0, w - 1); // unpad, clip brx
                            float yMax = Clamp((xyxy[3] - yPad) / gain, 0, h - 1); // unpad, clip bry

                            YoloLabel label = _model.Labels[scores.IndexOf(mulConfidence)];

                            var prediction = new YoloPrediction(label, mulConfidence)
                            {
                                Rectangle = new SKRect(xMin, yMin, xMax, yMax)
                            };

                            result.Add(prediction);
                        });
                    });
                });
            });

            return result.ToList();
        }

        /// <summary>
        /// Parses net outputs (sigmoid or detect layer) to predictions.
        /// </summary>
        /// <param name="output">The output from an inference call</param>
        /// <param name="image">The original input image</param>
        /// <returns>A list of Yolo predictions</returns>
        private List<YoloPrediction> ParseOutput(DenseTensor<float>[] output, SKImage image)
        {
            return _model.UseDetect ? ParseDetect(output[0], image) : ParseSigmoid(output, image);
        }

        /// <summary>
        /// Removes overlaped duplicates (nms).
        /// </summary>
        private List<YoloPrediction> Supress(List<YoloPrediction> items)
        {
            var result = new List<YoloPrediction>(items);

            foreach (var item in items) // iterate every prediction
            {
                foreach (var current in result.ToList()) // make a copy for each iteration
                {
                    if (current == item) continue;

                    var (rect1, rect2) = (item.Rectangle, current.Rectangle);

                    var intersection = SKRect.Intersect(rect1, rect2);

                    float intArea   = intersection.Area(); // intersection area
                    float unionArea = rect1.Area() + rect2.Area() - intArea; // union area
                    float overlap   = intArea / unionArea; // overlap ratio

                    if (overlap >= _model.Overlap)
                    {
                        if (item.Score >= current.Score)
                        {
                            result.Remove(current);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Runs object detection on an image
        /// </summary>
        /// <param name="image">The input image</param>
        /// <returns>A list of predictions</returns>
        public List<YoloPrediction> Predict(SKImage image)
        {
            return Supress(ParseOutput(Inference(image), image));
        }

        /// <summary>
        /// Creates new instance of YoloScorer.
        /// </summary>
        public YoloScorer()
        {
            _model = Activator.CreateInstance<T>();
        }

        /// <summary>
        /// Creates new instance of YoloScorer with weights path and options.
        /// </summary>
        public YoloScorer(string weights, SessionOptions opts = null) : this()
        {
            _inferenceSession = new InferenceSession(File.ReadAllBytes(weights), opts ?? new SessionOptions());
        }

        /// <summary>
        /// Creates new instance of YoloScorer with weights stream and options.
        /// </summary>
        public YoloScorer(Stream weights, SessionOptions opts = null) : this()
        {
            using (var reader = new BinaryReader(weights))
            {
                _inferenceSession = new InferenceSession(reader.ReadBytes((int)weights.Length), opts ?? new SessionOptions());
            }
        }

        /// <summary>
        /// Creates new instance of YoloScorer with weights bytes and options.
        /// </summary>
        public YoloScorer(byte[] weights, SessionOptions opts = null) : this()
        {
            _inferenceSession = new InferenceSession(weights, opts ?? new SessionOptions());
        }

        /// <summary>
        /// Disposes YoloScorer instance.
        /// </summary>
        public void Dispose()
        {
            _inferenceSession.Dispose();
        }
    }
}
