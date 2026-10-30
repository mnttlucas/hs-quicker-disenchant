using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HsQuickerDisenchant
{
	/// <summary>
	/// While the mouse is over the mod's window, the game is told that the mouse
	/// is far outside the screen, so a click on the window does not also click on the game behind it.
	/// It patches InputCollection.GetMousePosition, the game's central mouse-position getter (found with ILSpy:
	/// UnityInput implements IInput, which InputCollection uses). The window itself is not affected: IMGUI reads
	/// the mouse from Unity's own events.
	/// Can be turned off with [Window] BlockGameMouseOverWindow.
		/// Limit: only this getter is patched. If the game also reads the mouse another way, clicks may still pass through.
	/// </summary>
	public static class WindowInputBlocker
	{
		private static readonly Vector3 OffScreen3 = new Vector3(-10000f, -10000f, 0f);
		private static readonly Vector2 OffScreen2 = new Vector2(-10000f, -10000f);

		private static Rect _windowRect;
		private static bool _windowVisible;

		public static void SetVisible(bool visible)
		{
			_windowVisible = visible;
		}

		public static void SetRect(Rect rect)
		{
			_windowRect = rect;
		}

		public static void Install(Harmony harmony)
		{
			Type inputCollection = typeof(CollectionManager).Assembly.GetType("InputCollection");
			if (inputCollection == null)
				throw new InvalidOperationException("the type InputCollection was not found");

			MethodInfo original = FindMousePositionGetter(inputCollection);
			if (original == null)
				throw new InvalidOperationException("InputCollection.GetMousePosition() was not found, or it does not return a Vector2 or Vector3");

			string postfixName = original.ReturnType == typeof(Vector3) ? "PostfixVector3" : "PostfixVector2";
			MethodInfo postfix = typeof(WindowInputBlocker).GetMethod(postfixName, BindingFlags.Public | BindingFlags.Static);
			harmony.Patch(original, postfix: new HarmonyMethod(postfix));
		}

		private static MethodInfo FindMousePositionGetter(Type type)
		{
			BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
			foreach (MethodInfo method in type.GetMethods(flags))
			{
				if (method.Name != "GetMousePosition" || method.GetParameters().Length != 0)
					continue;
				if (method.ReturnType == typeof(Vector3) || method.ReturnType == typeof(Vector2))
					return method;
			}
			return null;
		}

		public static void PostfixVector3(ref Vector3 __result)
		{
			try
			{
				if (IsOverWindow(__result.x, __result.y))
					__result = OffScreen3;
			}
			catch (Exception)
			{
				// Never let a problem here disturb the game's own input.
			}
		}

		public static void PostfixVector2(ref Vector2 __result)
		{
			try
			{
				if (IsOverWindow(__result.x, __result.y))
					__result = OffScreen2;
			}
			catch (Exception)
			{
				// Never let a problem here disturb the game's own input.
			}
		}

		private static bool IsOverWindow(float mouseX, float mouseY)
		{
			if (!_windowVisible)
				return false;

			// Unity's screen origin is the bottom-left corner, IMGUI's is the top-left corner.
			return _windowRect.Contains(new Vector2(mouseX, Screen.height - mouseY));
		}
	}
}