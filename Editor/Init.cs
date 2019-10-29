// Copyright (c) 2019 ftrack
#define DEBUG_FTRACK 
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor.Scripting.Python;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityEditor.Ftrack.ConnectUnityEngine
{
    /// <summary>
    /// Exceptions thrown that relate to ftrack
    /// </summary>
    public class FtrackException : System.Exception
    {
        /// <summary>
        /// Constructor with message
        /// </summary>
        /// <param name="msg">The message of the exception</param>
        public FtrackException(string msg) : base(msg) { }
    }
    
    /// <summary>
    /// This class allows to interface with the out-of-process ftrack client
    /// </summary>
    public static class Client
    {
        private const string sessionStatePID = "ftrackClientPID";
        private const string name = "ftrack-connect-unity";
        private const double connectionTimeout = 10.0;
        private const double connectionSleepInterval = 0.01;
        private const int    spawnAttemptsLimit = 3;
        private static int   spawnAttemptsLeft  = spawnAttemptsLimit;

        private static bool ProcessExists()
        {
            if (PID == -1)
            {
                // Process was not started
                return false;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(PID);
            }
            catch (ArgumentException)
            {
                // Process is not running
                SessionState.EraseInt(sessionStatePID);
                return false;
            }

            // Process that are just done running need to be poked
            try
            {
                if (process.WaitForExit(0))
                {
                    // true means the process exited
                    return false;
                }
            }
            catch (SystemException)
            {
                // Process is not running
                SessionState.EraseInt(sessionStatePID);
                return false;
            }

            // The process is running normally
            return true;
        }

        internal static int PID
        {
            get
            {
                return SessionState.GetInt(sessionStatePID, -1);
            }
            set
            {
                SessionState.SetInt(sessionStatePID, value);
            }
        }

        internal static void EnsureClientProcess()
        {
            if (!ProcessExists())
            {
                // Spawn the client
                string resourcePath = System.Environment.GetEnvironmentVariable("FTRACK_UNITY_RESOURCE_PATH");
                if (null == resourcePath)
                {
                    throw new FtrackException("FTRACK_UNITY_RESOURCE_PATH was not found in the environment. Make sure to launch Unity from ftrack-connect to use the ftrack integration");
                }

                string clientModule = $"{resourcePath}/scripts/ftrack_client.py";
                using (Py.GIL())
                {
                    dynamic pOpenObj = PythonRunner.SpawnClient(clientModule);
                    Client.PID = pOpenObj.pid;
        
                    spawnAttemptsLeft--;
                }
            }

            // The process exists. Give it some time to connect in case it was 
            // just spawned
            var waitEnum = PythonRunner.WaitForConnection(name, connectionTimeout);

            dynamic timeModule;
            using (Py.GIL())
            {
                timeModule = PythonEngine.ImportModule("time");
            }

            while (waitEnum.MoveNext())
            {
                using(Py.GIL())
                {
                    // This wakes up the Python interpreter, allowing the 
                    // client to connect
                    timeModule.sleep(connectionSleepInterval);
                }
            }

            if (!PythonRunner.IsClientConnected(name))
            {
                // The process runs, but the client never connected. 
                // Try again
                if (spawnAttemptsLeft > 0)
                {
                    // Try again
                    PID = -1;
                    EnsureClientProcess();
                }
                else
                {
                    throw new FtrackException("The ftrack client could not connect successfully.");
                }
            }

            // The client successfully connected
            // Prepare for next time
            spawnAttemptsLeft = spawnAttemptsLimit;
        }

        /// <summary>
        /// Calls a service on the ftrack client
        /// </summary>
        /// <param name="serviceName">The name of the service to call</param>
        /// <param name="args">The arguments that will be passed to the service</param>
        public static void CallService(string serviceName, params object[] args)
        {
            EnsureClientProcess();
            _ = PythonRunner.CallAsyncServiceOnClient(name, serviceName, args);
        }
    }

    /// <summary>
    /// This class is responsible for initializing the ftrack client
    /// </summary>
    public static class Init
    {

        [InitializeOnLoadMethod]
        private static void OnInit()
        {
            // Installing a delayCall will help with avoiding domain reloads 
            // during initialization
            EditorApplication.delayCall += InitFtrack;
        }

        private static void InitFtrack()
        {
            // Wait until the Python Engine is fully initialized
            if (!PythonEngine.IsInitialized)
            {
                EditorApplication.delayCall += InitFtrack;
                return;
            }

            // Register for clean-up on exit
            EditorApplication.quitting += OnQuit;
            Client.EnsureClientProcess();
        }

        private static void OnQuit()
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
            PythonRunner.StopServer(false);
            PythonRunner.StartServer();
            Init.InitFtrack();
        }
#endif // DEBUG_FTRACK
    }

    /// <summary>
    /// This class is responsible for performing costly operations on the 
    /// server-side (In Unity instead of in the client)
    /// </summary>
    public class ServerSideUtils
    {
        private const string exportPackageProgressBarTitle = "Exporting assets";
        /// <summary>
        /// Will select the assests matching the passed guids
        /// </summary>
        /// <param name="guids">The guids corresponding to the assets to select</param>
        public static void SelectObjectsWithGuids(string [] guids)
        {
            // Validate assets guids first
            var assetsToSelect = new List<UnityEngine.Object>();
            foreach (var guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset)
                {
                    assetsToSelect.Add(asset);
                }
            }
            
            // Select the objects   
            UnityEditor.Selection.objects = assetsToSelect.ToArray();

            // Focus the selected assets.
            // This does not work well on multi-selection (only the last 
            // element will be pinged and highlighted in yellow).
            // This is why we only ping the first asset in the list.
            //
            // The final result is that all the assets are selected, but only 
            // the first one is focused.
            if (assetsToSelect.Count > 0)
            {
                EditorGUIUtility.PingObject(assetsToSelect[0]);
            }
        }

        internal struct ImportInfo
        {
            internal ImportInfo(PyObject assetData, string destinationDirectory, PyObject options)
            {
                this.assetData = assetData;
                this.destinationDirectory = destinationDirectory;
                this.options = options;
            }
                
            internal PyObject assetData;
            internal string destinationDirectory;
            internal PyObject options;
        }
        static private List<ImportInfo> assetsToImport = new List<ImportInfo>();

        /// <summary>
        /// Prepares importing an asset. The actual import operation will be 
        /// scheduled in a future delayCall to DoImportAssets
        /// </summary>
        /// <param name="jsonArgs">JSON string of a dictionary containing the 
        /// following keys: "asset_data" (the asset metadata), 
        /// "options" (import options), "dst_directory" (the target directory 
        /// where the asset should be imported)</param>
        public static void ImportAsset(PyObject jsonArgs)
        {
            // unpack the arguments
            using (Py.GIL())
            {
                dynamic jsonModule = Py.Import("json");
                dynamic arguments = jsonModule.loads(jsonArgs);

                PyObject assetData          = arguments["asset_data"];
                PyObject options            = arguments["options"];
                string destinationDirectory = arguments["dst_directory"];
                assetsToImport.Add(new ImportInfo(assetData, destinationDirectory, options));
            }

            EditorApplication.delayCall += DoImportAssets;
        }
        private static void DoImportAssets()
        {
            while (assetsToImport.Count > 0)
            {
                var importInfo = assetsToImport[0];
                assetsToImport.RemoveAt(0);

                string assetName;
                string sourceFile;
                using (Py.GIL())
                {
                    dynamic meta = importInfo.assetData;
                    assetName = meta["assetName"];
                    sourceFile = meta["filePath"];
                }
                
                // The destination directory must be under Assets/
                String destinationDirectory = importInfo.destinationDirectory;
                
                // Unity prefers forward slashes
                sourceFile = sourceFile.Replace("\\","/");
                destinationDirectory = destinationDirectory.Replace("\\","/");

                if (destinationDirectory.IndexOf(Application.dataPath) != 0)
                {
                    throw new ArgumentException($"Cannot import asset \"{assetName}\": " + 
                        $"the destination directory (\"{destinationDirectory}\") " +
                        $"must be located under \"{Application.dataPath}\"");
                }

                if (string.IsNullOrEmpty(destinationDirectory))
                {
                    throw new ArgumentNullException($"Cannot import asset \"{assetName}\": " +
                        $"The specified destination directory was not specified");
                }

                // Use the asset name as the destination file name
                string extension = Path.GetExtension(sourceFile);
                string destinationFile = destinationDirectory + "/" + assetName + extension;

                // If the asset already exists, ask if it is ok to overwrite
                if (File.Exists(destinationFile))
                {
                    var result = EditorUtility.DisplayDialog(
                        "ftrack Asset Already Exists",
                        "This asset already exists in the project!\n" +
                        "Do you want to reimport this asset?",
                        "Yes", "No");

                    if (!result)
                    {
                        // Skip to the next asset to import
                        continue;
                    }
                }

                try
                {
                    // Create the directory
                    Directory.CreateDirectory(destinationDirectory);
                }
                catch (Exception)
                {
                    UnityEngine.Debug.LogError($"Cannot import asset " +
                        $"\"{assetName}\": unable to create directory " +
                        $"\"{destinationDirectory}\" ");

                    throw;
                }
                try
                {
                    // Copy the file
                    File.Copy(sourceFile, destinationFile, overwrite:true);
                }
                catch (Exception)
                {
                    UnityEngine.Debug.LogError($"Cannot import asset " +
                        $"\"{assetName}\": unable to copy \"{sourceFile}\" " +
                        $"to \"{destinationFile}\" ");

                    throw;
                }
                    
                // Refresh the asset database to get the related model importer
                AssetDatabase.Refresh();

                // Add the ftrack metadata to the model importer
                // The Asset Importer expects a path using forward slashes and starting
                // with 'Assets/'
                String modelImporterPath = destinationFile;
                modelImporterPath = "Assets" + modelImporterPath.Substring(Application.dataPath.Length);

                ModelImporter modelImporter = AssetImporter.GetAtPath(modelImporterPath) as ModelImporter;
                if (!modelImporter)
                {
                    throw new FtrackException($"Could not find the asset importer " +
                        $"for {destinationFile}");
                }

                // Generate a json string from the metadata
                string jsonString;
                using (Py.GIL())
                {
                    dynamic jsonModule = Py.Import("json");
                    jsonString = jsonModule.dumps(importInfo.assetData);
                }

                // Add the metadata
                modelImporter.userData = jsonString;

                // Set the options
                using (Py.GIL())
                {
                    // No easy way to check for None
                    PyObject Py_None = PyObject.FromManagedObject(null);
                    if (importInfo.options.ToString() != Py_None.ToString())
                    {
                        // Apply the options
                        dynamic options = importInfo.options;
                        dynamic unityImportMaterials = options.get("unityImportMaterials", null);
                        dynamic unityImportAnim      = options.get("unityImportAnim", null);
                        dynamic unityAnimType        = options.get("unityAnimType", null);
                        dynamic unityLoopTime        = options.get("unityLoopTime", null);

                        if (unityImportAnim != null)
                        {
                            modelImporter.importAnimation = (bool) unityImportAnim;
                        }

                        if (unityImportMaterials != null)
                        {
                            modelImporter.importMaterials = (bool) unityImportMaterials;
                        }

                        if (unityAnimType != null)
                        {
                            string animType = ((PyObject)unityAnimType).ToString();
                            if (animType == "None")
                            {
                                modelImporter.animationType = ModelImporterAnimationType.None;
                            }
                            else if (animType == "Legacy")
                            {
                                modelImporter.animationType = ModelImporterAnimationType.Legacy;
                            }
                            else if (animType == "Generic")
                            {
                                modelImporter.animationType = ModelImporterAnimationType.Generic;
                            }
                            else if (animType == "Human")
                            {
                                modelImporter.animationType = ModelImporterAnimationType.Human;
                            }
                            else
                            {
                                UnityEngine.Debug.LogWarning($"Unknown " +
                                    $"animation type specified: " +
                                    $"\"{animType}\". Defaulting to " +
                                    $"\"None\"");
                                    modelImporter.animationType = ModelImporterAnimationType.None;
                            }
                        }

                        if (unityLoopTime != null)
                        {
                            // Set the "Loop Time" property on the imported clips
                            var clipAnimations = modelImporter.defaultClipAnimations;
                            foreach (var clip in clipAnimations)
                            {
                                clip.loopTime = (bool) unityLoopTime;
                            }
                            modelImporter.clipAnimations = clipAnimations;
                        }
                    }
                }

                modelImporter.SaveAndReimport();

                UnityEngine.Debug.Log($"Successfully imported {assetName} " +
                    $"({sourceFile} -> {destinationFile}");
            }
        }
        
        internal struct PublishInfo
        {
            internal PublishInfo(string assetType, bool publishPackage)
            {
                this.assetType = assetType;
                this.publishPackage = publishPackage;
            }

            internal string assetType;
            internal bool publishPackage;
        }
        /// <summary>
        /// Prepares the publish components and calls the client when the work 
        /// is done
        /// </summary>
        /// <param name="jsonArgs">JSON string of a dictionary containing the 
        /// following keys: 'asset_type', 'options'</param>
        public static void Publish(PyObject jsonArgs)
        {
            // unpack the arguments
            PublishInfo publishInfo = new PublishInfo();
            using (Py.GIL())
            {
                dynamic jsonModule = Py.Import("json");
                dynamic arguments = jsonModule.loads(jsonArgs);
                publishInfo.assetType = arguments["asset_type"];

                dynamic options = arguments["options"];
                publishInfo.publishPackage = options["publishPackage"];
            }

            // Start with the recordings. The package component will get 
            // generated after recording is done
            GenerateRecordings(publishInfo);
        }
        internal static void GenerateRecordings(PublishInfo info)
        {
            // This method initiates the Unity recordings by calling into the 
            // Recorder. Once it is done recording, the Recorder will 
            // call RecordingDone
            if (info.assetType != "img")
            {
                MovieRecorder.Record(info);
            } 
            else 
            {
                ImageSequenceRecorder.Record(info);
            }
        }

        internal static void RecordingDone(PublishInfo info, PyObject publishArgs)
        {
            // Called by the Recorder when it is done recording
            // Will export the selection as a package if required, then call 
            // into the client to complete the publish
            if (info.publishPackage)
            {
                string filePath = System.IO.Path.GetTempPath() + "/" + 
                    Application.productName + "/" + "currentScene.unitypackage";
                
                bool exported = ExportCurrentScene(filePath);

                // Prepare export results for the client
                using (Py.GIL())
                {
                    dynamic args = publishArgs;
                    args["success"] = exported.ToPython();

                    if (exported)
                    {
                        args["package_filepath"] = filePath.ToPython();
                    }
                    else
                    {
                        string errorMsg = "Could not export the current scene. " +
                            "Make sure a scene is loaded before publishing";
                        args["error_msg"] = errorMsg.ToPython();
                    }
                }
            }

            // Call the client
            Client.CallService("publish", publishArgs);
        }

        internal static bool ExportCurrentScene(string targetFilePath)
        {
            var currentScene = SceneManager.GetActiveScene();
            if (!currentScene.IsValid())
            {
                return false;
            }
            
            EditorUtility.DisplayProgressBar(
                exportPackageProgressBarTitle,
                $"Exporting scene: {currentScene.name}",
                0.1f);

            try
            {
                // Export the package
                AssetDatabase.ExportPackage(currentScene.path, 
                    targetFilePath,
                    ExportPackageOptions.IncludeDependencies);
            }
            catch (UnityException e)
            {
                UnityEngine.Debug.LogException(e);
                EditorUtility.ClearProgressBar();

                return false;
            }

            EditorUtility.ClearProgressBar();
            return true;
        }
    }
}