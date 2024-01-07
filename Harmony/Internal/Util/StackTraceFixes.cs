using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib.Tools;
using MonoMod.Core;
using MonoMod.Core.Platforms;
using MonoMod.RuntimeDetour;
using System.Linq;

namespace HarmonyLib.Internal.RuntimeFixes
{
	/// <summary>
	/// Patching methods potentially messes up the stack.
	/// Especially calls to GetExecutingAssembly won't turn in correct methods
	/// </summary>
	internal static class StackTraceFixes
	{
		private static bool _applied;

		private static readonly Dictionary<MethodBase, MethodBase> RealMethodMap =
			new Dictionary<MethodBase, MethodBase>();

		private static Hook getAssemblyHookManaged;
		private static ICoreDetour getAssemblyDetour;
		private static Hook getMethodHook;

		public static void Install()
		{
			if (_applied)
				return;

			try
			{
				DetourManager.ILHookApplied += OnILChainRefresh;
				DetourManager.ILHookUndone += OnILChainRefresh;

				// Must be done first to avoid race condition.
				getMethodHook = new Hook(AccessTools.DeclaredMethod(typeof(StackFrame), nameof(StackFrame.GetMethod), EmptyType.NoArgs), GetMethodFix);

				var getAssemblyMethod = AccessTools.DeclaredMethod(typeof(Assembly), nameof(Assembly.GetExecutingAssembly), EmptyType.NoArgs);
				if (getAssemblyMethod.HasMethodBody())
				{
					getAssemblyHookManaged = new Hook(getAssemblyMethod, GetAssemblyFix);
				}
				else
				{
					var getAssemblyFixNative = AccessTools.Method(typeof(StackTraceFixes), nameof(GetAssemblyFixNative));
					getAssemblyDetour = DetourFactory.Current.CreateDetour(getAssemblyMethod, getAssemblyFixNative);
					RealMethodMap.Add(PlatformTriple.Current.GetIdentifiable(getAssemblyFixNative), PlatformTriple.Current.GetIdentifiable(getAssemblyMethod));
				}
			}
			catch (Exception e)
			{
				Logger.LogText(Logger.LogChannel.Error, $"Failed to apply stack trace fix: ({e.GetType().FullName}) {e.Message}");
			}
			_applied = true;
		}

		delegate Assembly GetAssemblyDelegate();

		// We need to force GetExecutingAssembly make use of stack trace
		// This is to fix cases where calling assembly is actually the patch
		// This solves issues with code where it uses the method to get current filepath etc
		private static Assembly GetAssemblyFix(GetAssemblyDelegate orig)
		{
			var entry = getAssemblyHookManaged.DetourInfo.Entry;
			var method = new StackTrace().GetFrames()!.Select(f => f.GetMethod()).SkipWhile(m => m != entry).Skip(1).First();
			return method.Module.Assembly;
		}

		private static Assembly GetAssemblyFixNative()
		{
			var original = getAssemblyDetour.Source;
			var method = new StackTrace().GetFrames()!.Select(f => f.GetMethod()).SkipWhile(m => m != original).Skip(1).First();
			return method.Module.Assembly;
		}

		private static MethodBase GetMethodFix(Func<StackFrame, MethodBase> orig, StackFrame self)
		{
			var method = orig(self);
			if (method is not null && RealMethodMap.TryGetValue(PlatformTriple.Current.GetIdentifiable(method), out var real))
			{
				return real;
			}
			return method;
		}

		private static readonly AccessTools.FieldRef<MethodDetourInfo, object> GetDetourState = AccessTools.FieldRefAccess<MethodDetourInfo, object>(AccessTools.DeclaredField(typeof(MethodDetourInfo), "state"));

		private static readonly AccessTools.FieldRef<object, MethodBase> GetEndOfChain =
			AccessTools.FieldRefAccess<object, MethodBase>(AccessTools.DeclaredField(typeof(DetourManager).GetNestedType("ManagedDetourState", AccessTools.all), "EndOfChain"));

		// Helper to save the detour info after patch is complete
		private static void OnILChainRefresh(ILHookInfo self)
		{
			lock (RealMethodMap)
			{
				RealMethodMap[PlatformTriple.Current.GetIdentifiable(GetEndOfChain(GetDetourState(self.Method)))] = PlatformTriple.Current.GetIdentifiable(self.Method.Method);
			}
		}
	}
}
