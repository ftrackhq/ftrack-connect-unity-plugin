// Copyright (c) 2019 ftrack

#define DEBUG_FTRACK 

using UnityEditor;
using UnityEngine;
using UnityEditor.Scripting.Python;
using System.IO;

namespace UnityEditor.ftrack.connect_unity_engine
{
    public static class Init
    {
        [InitializeOnLoadMethod]
        private static void InitFtrack()
        {
            // Register for clean-up on exit
            EditorApplication.quitting += OnQuit;

            string resourcePath = System.Environment.GetEnvironmentVariable("FTRACK_UNITY_RESOURCE_PATH");
            if (null == resourcePath)
            {
                Debug.LogError("FTRACK_UNITY_RESOURCE_PATH was not found in the environment. Make sure to launch Unity from ftrack-connect to use the ftrack integration");
                return;
            }

            // ftrack runs in the client process. This Python script is 
            // responsible to control ftrack (open dialogs, init, …)
            string initModule = Path.Combine(resourcePath, "scripts", "ftrack_client_init.py");

            PythonRunner.StartServer(initModule);
            PythonRunner.CallServiceOnClient("'ftrack_load_and_init'");
        }

        static void OnQuit()
        {
            EditorApplication.quitting -= OnQuit;

            // Remove the Assets/ftrack directory
            string ftrackAssetPath = UnityEngine.Application.dataPath;
            ftrackAssetPath = Path.Combine(ftrackAssetPath, "ftrack/Temp");
            try
            {
                Directory.Delete(ftrackAssetPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        #if DEBUG_FTRACK
        [MenuItem("ftrack/Debug/Re-initialize")]
        private static void ReInit()
        {
            // Stop the server and client
            PythonRunner.StopServer(true);
            InitFtrack();
        }
#endif // DEBUG_FTRACK
    }
}