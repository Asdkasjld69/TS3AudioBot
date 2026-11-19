// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace TS3AudioBot.Helper
{
	internal static class ImageUtil
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		public const int ResizeMaxWidthDefault = 320;

		public static async Task<ImageHolder> ResizeImageSave(Stream imgStream, int resizeMaxWidth = ResizeMaxWidthDefault)
		{
			try
			{
				using var limitStream = new LimitStream(imgStream, Limits.MaxImageStreamSize);
				using var mem = new MemoryStream();
				await limitStream.CopyToAsync(mem);
				mem.Seek(0, SeekOrigin.Begin);
				return ResizeImage(mem, resizeMaxWidth);
			}
			catch (ImageFormatException)
			{
				Log.Debug("Dropping image because of unknown format");
				throw Error.LocalStr("Dropping image because of unknown format"); // TODO
			}
			catch (EntityTooLargeException)
			{
				Log.Debug("Dropping image because too large");
				throw Error.LocalStr("Dropping image because too large"); // TODO
			}
		}

		private static ImageHolder ResizeImage(Stream imgStream, int resizeMaxWidth)
		{
			using var img = Image.Load(imgStream);
			if (img.Width > Limits.MaxImageDimension || img.Height > Limits.MaxImageDimension
				|| img.Width == 0 || img.Height == 0)
				throw Error.LocalStr("Dropping image because too large"); // TODO

			if (img.Width <= resizeMaxWidth)
				return SaveAdaptive(img);

			float ratio = img.Width / (float)img.Height;
			img.Mutate(x => x.Resize(resizeMaxWidth, (int)(resizeMaxWidth / ratio)));

			return SaveAdaptive(img);
		}

		private static ImageHolder SaveAdaptive(Image img)
		{
			IImageFormat format;
			if (img.Frames.Count > 1)
			{
				format = GifFormat.Instance;
			}
			else
			{
				format = JpegFormat.Instance;
			}
			var mime = format.DefaultMimeType;
			var mem = new MemoryStream();
			img.Save(mem, format);
			mem.Seek(0, SeekOrigin.Begin);
			return new ImageHolder(mem, mime);
		}

	/// <summary>
	/// 按目标字节上限压缩图片，返回从"质量最好"到"最能塞下"的降级序列。
	/// TS3 服务端用 i_client_max_avatar_filesize 卡头像大小，超了会回
	/// permission_invalid_size；调用方拿这个序列逐个上传，第一个成功即止。
	/// 320px/75 质量的封面常有 20-40KB，而服务器允许值可能只有几 KB。
	/// </summary>
	private static List<byte[]> CompressToFitImpl(byte[] source, long maxBytes)
	{
		var result = new List<byte[]>();
		if (source == null || source.Length == 0)
			return result;

		bool animated;
		try
		{
			using var probe = Image.Load(source);
			animated = probe.Frames.Count > 1;
		}
		catch (ImageFormatException)
		{
			return result;
		}

		// 宽 × 质量 的降级梯度。前面保清晰度，后面快速缩小。
		var widths = new[] { 320, 240, 180, 128, 96, 64 };
		var qualities = animated ? new[] { 0 } : new[] { 75, 60, 45 };

		foreach (var width in widths)
		{
			foreach (var quality in qualities)
			{
				byte[] encoded;
				try
				{
					using var img = Image.Load(source);
					if (img.Width > width)
					{
						float ratio = img.Width / (float)img.Height;
						var h = Math.Max(1, (int)(width / ratio));
						img.Mutate(x => x.Resize(width, h));
					}

					using var ms = new MemoryStream();
					if (animated)
						img.Save(ms, GifFormat.Instance);
					else
						img.Save(ms, new JpegEncoder { Quality = quality });

					encoded = ms.ToArray();
				}
				catch (Exception ex)
				{
					Log.Debug(ex, "CompressToFit: 编码失败（{0}px/q{1}）", width, quality);
					continue;
				}

				if (result.Count > 0 && result[result.Count - 1].Length == encoded.Length)
					continue;
				result.Add(encoded);

				if (maxBytes > 0 && encoded.Length <= maxBytes)
					return result;
			}
		}

		return result;
	}

	public static List<byte[]> CompressToFit(byte[] source, long maxBytes)
		=> CompressToFitImpl(source, maxBytes);
	}

	class ImageHolder : IDisposable
	{
		public Stream Stream { get; }
		public string Mime { get; }

		public ImageHolder(Stream stream, string mime)
		{
			Stream = stream;
			Mime = mime;
		}

		public void Dispose() => Stream.Dispose();
	}
}
