using System;
using System.Reflection;
using UnityEngine;

namespace HsQuickerDisenchant
{
	/// <summary>
	/// Makes the window background more opaque by drawing a dark rectangle over the game's default translucent one.
	/// The game's Unity DLLs are trimmed, so GUI.DrawTexture is called through reflection: if it is missing,
	/// the window simply keeps its original look.
	/// </summary>
	public sealed class WindowBackground
	{
		private static readonly Color Tint = new Color(0.12f, 0.12f, 0.12f, 1f);

		private readonly float _opacity;
		private Texture2D _texture;
		private MethodInfo _drawTexture;
		private bool _resolved;

		/// <param name="opacity">0 keeps the original look, 1 is fully opaque.</param>
		public WindowBackground(float opacity)
		{
			_opacity = Math.Max(0f, Math.Min(1f, opacity));
		}

		/// <summary>Draws the layer. Call it first inside the window function (coordinates are the window's own).</summary>
		public void Draw(Rect area)
		{
			if (_opacity <= 0f)
				return;

			if (!_resolved)
			{
				_resolved = true;
				_drawTexture = typeof(GUI).GetMethod("DrawTexture", new Type[] { typeof(Rect), typeof(Texture) });
				if (_drawTexture == null)
					Plugin.Log.LogWarning("GUI.DrawTexture is not available in this game's Unity DLLs: the window keeps its original look.");
				else
					_texture = MakeTexture(new Color(Tint.r, Tint.g, Tint.b, _opacity));
			}
			if (_drawTexture == null)
				return;

			try
			{
				_drawTexture.Invoke(null, new object[] { area, _texture });
			}
			catch (Exception exception)
			{
				_drawTexture = null;
				Plugin.Log.LogWarning("Could not draw the window background: " + exception.Message);
			}
		}

		private static Texture2D MakeTexture(Color color)
		{
			Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
			texture.SetPixel(0, 0, color);
			texture.Apply();
			texture.hideFlags = HideFlags.HideAndDontSave;
			return texture;
		}
	}
}