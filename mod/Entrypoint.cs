using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Doorstop
{
    // UnityDoorstop entry point: runs before any game code. Only patch here; Unity is not up yet.
    public static class Entrypoint
    {
        public static void Start()
        {
            PhoenixBattles.Log.Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            PhoenixBattles.Log.Line("loaded from " + PhoenixBattles.Log.Dir);
            // Touching Unity types before the engine has registered its internal calls breaks them
            // (MissingMethodException on GameObject.SetActive), so patch only once Unity loads the game assembly.
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                if (e.LoadedAssembly.GetName().Name != "Assembly-CSharp") return;
                try
                {
                    new Harmony("phoenixbattles").PatchAll(typeof(Entrypoint).Assembly);
                    PhoenixBattles.Log.Line("harmony patches applied");
                }
                catch (Exception ex) { PhoenixBattles.Log.Line("patch failed: " + ex); }
            };
        }
    }
}

namespace PhoenixBattles
{
    public static class Log
    {
        public static string Dir = ".";
        public static void Line(string s)
        {
            try { File.AppendAllText(Path.Combine(Dir, "PhoenixBattles.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\n"); } catch { }
        }
    }
}
