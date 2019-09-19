using System.Reflection;
using UnityEditor.Recorder;
using System.Collections;
using UnityEditor.Scripting.Python;

namespace UnityEditor.ftrack
{
    public class ImageSequenceRecorder : FtrackRecorder<ImageRecorderSettings> {
        private static RecorderSettings s_movieRecorderSettings = null;
        private static string s_origMovieFilePath = null;
        private static readonly string s_movieFilename = "reviewable";

        private static RecorderSettings MovieRecorderSettings
        {
            get
            {
                if (s_movieRecorderSettings == null)
                {
                    s_movieRecorderSettings = GetRecorder<MovieRecorderSettings>();
                }
                return s_movieRecorderSettings;
            }
        }

        protected static string MovieRecorderPath
        {
            get {
                if(MovieRecorderSettings == null)
                {
                    return null;
                }
                return MovieRecorderSettings.outputFile;
            }
            set
            {
                if(MovieRecorderSettings == null)
                {
                    return;
                }
                MovieRecorderSettings.outputFile = value;
            }
        }

        /// <summary>
        /// We must install the delegate on each domain reload
        /// </summary>
        [InitializeOnLoadMethod]
        private static void OnReload()
        {
            s_filename = "frame.<Frame>";
            s_lockFileName = ".ImageRecordTimeline.lock";
            if (IsRecording)
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChange;
            }
        }

        public static void Record()
        {
            IsRecording = true;

            s_origFilePath = RecorderPath;
            RecorderPath = GetTempFilePath();
            
            s_origMovieFilePath = MovieRecorderPath;
            MovieRecorderPath = System.IO.Path.Combine(GetTempFolderPath(), s_movieFilename);

            // Delete the temp folder if it already exists
            string folderPath = GetTempFolderPath();
            if (System.IO.Directory.Exists(folderPath))
            {
                System.IO.Directory.Delete(folderPath, true);
            }

            EditorApplication.playModeStateChanged += OnPlayModeStateChange;

            StartRecording();
        }

        protected static void OnPlayModeStateChange(PlayModeStateChange state)
        {
            if (IsRecording)
            {
                // Domain reloads lose the overriden Recorder path. We know a 
                // domain reload occurred if m_origFilePath is not set (cleared 
                // by a domain reload)
                if (null == s_origFilePath)
                {
                    s_origFilePath = RecorderPath;
                    RecorderPath = GetTempFilePath();
                }

                if(null == s_origMovieFilePath)
                {
                    s_origMovieFilePath = MovieRecorderPath;
                    MovieRecorderPath = System.IO.Path.Combine(GetTempFolderPath(), s_movieFilename);
                }

                if (state == PlayModeStateChange.EnteredEditMode)
                {
                    // Publish with ftrack
                    string publishArgs = null;
                    if (MovieRecorderSettings == null)
                    {
                        publishArgs = string.Format(
                            "{{'image_path':'{0}', 'image_ext':'{1}'}}",
                            RecorderPath, RecorderSettings.extension);
                    }
                    else
                    {
                        publishArgs = string.Format(
                            "{{'image_path':'{0}', 'image_ext':'{1}', 'movie_path':'{2}', 'movie_ext':'{3}'}}",
                            RecorderPath, RecorderSettings.extension,
                            MovieRecorderPath, MovieRecorderSettings.extension);
                    }

                    PythonRunner.CallServiceOnClient("'publish_callback'", string.Format("\"{0}\"", publishArgs));

                    EditorApplication.playModeStateChanged -= OnPlayModeStateChange;
                    RecorderPath = s_origFilePath;
                    MovieRecorderPath = s_origMovieFilePath;
                    IsRecording = false;
                }
            }
        }
    }

    public class MovieRecorder : FtrackRecorder<MovieRecorderSettings> {
        /// <summary>
        /// We must install the delegate on each domain reload
        /// </summary>
        [InitializeOnLoadMethod]
        private static void OnReload()
        {
            s_filename = "reviewable";
            s_lockFileName = ".MovieRecordTimeline.lock";
            if (IsRecording)
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChange;
            }
        }

        public static void Record()
        {
            IsRecording = true;

            s_origFilePath = RecorderPath;

            RecorderPath = GetTempFilePath();

            // Delete the temp folder if it already exists
            string folderPath = GetTempFolderPath();
            if (System.IO.Directory.Exists(folderPath))
            {
                System.IO.Directory.Delete(folderPath, true);
            }

            EditorApplication.playModeStateChanged += OnPlayModeStateChange;

            StartRecording();
        }

        protected static void OnPlayModeStateChange(PlayModeStateChange state)
        {
            if (IsRecording)
            {
                // Domain reloads lose the overriden Recorder path. We know a 
                // domain reload occurred if m_origFilePath is not set (cleared 
                // by a domain reload)
                if (null == s_origFilePath)
                {
                    s_origFilePath = RecorderPath;
                    RecorderPath = GetTempFilePath();
                }

                if (state == PlayModeStateChange.EnteredEditMode)
                {
                    // Publish with ftrack
                    PythonRunner.CallServiceOnClient("'publish_callback'", string.Format(
                        "\"{{'movie_path':'{0}', 'movie_ext':'{1}'}}\"",
                        RecorderPath, RecorderSettings.extension));

                    EditorApplication.playModeStateChanged -= OnPlayModeStateChange;
                    RecorderPath = s_origFilePath;
                    IsRecording = false;
                }
            }
        }
    }

    public class FtrackRecorder<T> where T : RecorderSettings
    {
        protected static string s_origFilePath = null;
        private static RecorderSettings s_recorderSettings = null;
        protected static string s_filename = "test";
        protected static string s_lockFileName = ".RecordTimeline.lock";

        private static string LockFilePath { get { return GetTempFolderPath() + s_lockFileName; } }
        protected static bool IsRecording
        {
            get
            {
                return System.IO.File.Exists(LockFilePath);
            }
            set
            {
                if (value == true)
                {
                    System.IO.File.Create(LockFilePath);
                }
                else
                {
                    if (System.IO.File.Exists(LockFilePath))
                    {
                        System.IO.File.Delete(LockFilePath);
                    }
                }
            }
        }

        /// <summary>
        /// Returns a deterministic path based on the project name
        /// e.g. %TEMP%\Unity_Project_Name for the Windows platform
        /// </summary>
        /// <returns>The path</returns>
        protected static string GetTempFilePath()
        {
            // Note: not combining using System.IO.Path as an error is raised
            //       because <> characters are not permitted in paths, although they
            //       are required for recording image sequences.
            return GetTempFolderPath() + "/" + s_filename;
        }

        protected static string GetTempFolderPath()
        {
            // store to a temporary path, to delete after publish
            var tempPath = System.IO.Path.GetTempPath();

            // TODO: what should the name of the video file be?
            tempPath = System.IO.Path.Combine(tempPath, UnityEngine.Application.productName);

            return tempPath;
        }

        private static object GetFieldValue(string fieldName, object from)
        {
            FieldInfo fi = from.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return fi.GetValue(from);
        }

        private static object GetPropertyValue(string propName, object from, BindingFlags bindingFlags = BindingFlags.NonPublic | BindingFlags.Instance)
        {
            PropertyInfo propInfo = from.GetType().GetProperty(propName, bindingFlags);
            return propInfo.GetValue(from);
        }

        protected static RecorderSettings RecorderSettings
        {
            get
            {
                if (s_recorderSettings == null)
                {
                    s_recorderSettings = GetRecorder<T>();
                    if (s_recorderSettings == null)
                    {
                        UnityEngine.Debug.LogError("Could not find a valid MovieRecorder");
                    }
                }
                return s_recorderSettings;
            }
        }

        protected static string RecorderPath
        {
            get { return RecorderSettings.outputFile; }
            set
            {
                RecorderSettings.outputFile = value;
            }
        }

        protected static void StartRecording()
        {
            var recorderWindow = EditorWindow.GetWindow<RecorderWindow>();
            if (!recorderWindow)
            {
                return;
            }
            // start recording
            recorderWindow.StartRecording();
        }

        protected static RecorderSettings GetRecorder<U>() where U : RecorderSettings
        {
            var recorderWindow = EditorWindow.GetWindow<RecorderWindow>();
            if (!recorderWindow)
            {
                return null;
            }

            // first try to get the selected item, if it's not a MovieRecorder,
            // then go through the list and try to find one that is called "ftrack".
            // if there isn't one then just take one of the MovieRecorders.
            var selectedRecorder = GetFieldValue("m_SelectedRecorderItem", recorderWindow);
            if (selectedRecorder != null)
            {
                RecorderSettings recorderSettings = GetPropertyValue("settings", selectedRecorder, BindingFlags.Public | BindingFlags.Instance) as RecorderSettings;
                if (recorderSettings.GetType().Equals(typeof(U)))
                {
                    // found movie recorder settings
                    return recorderSettings as U;
                }
            }

            var recorderList = GetFieldValue("m_RecordingListItem", recorderWindow);
            var itemList = (IEnumerable)GetPropertyValue("items", recorderList, BindingFlags.Public | BindingFlags.Instance);
            U movieRecorder = null;
            foreach (var item in itemList)
            {
                RecorderSettings settings = GetPropertyValue("settings", item, BindingFlags.Public | BindingFlags.Instance) as RecorderSettings;
                var recorder = settings as U;
                if (recorder == null)
                {
                    continue;
                }
                movieRecorder = recorder;

                var editableLabel = GetFieldValue("m_EditableLabel", item);
                var labelText = (string)GetPropertyValue("text", editableLabel);
                if (labelText.Equals("ftrack"))
                {
                    return movieRecorder;
                }
            }
            return movieRecorder;
        }

        public static void ApplySettings(int start, int end, float fps)
        {
            var window = EditorWindow.GetWindow<RecorderWindow>(
                false,"Recorder", false
            );
            if (!window)
                return;

            // Get the settings through reflection        
            var field = window.GetType().GetField("m_ControllerSettings",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var settings = field.GetValue(window) as RecorderControllerSettings;
            settings.SetRecordModeToFrameInterval(start, end);
            
            // Get the dictionary of frame rate options to float values
            var frameRateDictField = settings.GetType().GetField("s_FPSToValue",
                BindingFlags.NonPublic | BindingFlags.Static);

            var frameRateDict = frameRateDictField.GetValue(null) 
                as System.Collections.IDictionary;

            // Get the frame rate type field to set with 
            // the appropriate enum value
            var frameRateTypeField = settings.GetType().GetField(
                "m_FrameRateType",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            bool setValue = false;
            foreach (DictionaryEntry keyValuePair in frameRateDict)
            {
                float value = (float)keyValuePair.Value;
                if(UnityEngine.Mathf.Abs(fps - value) < 0.01f)
                {
                    frameRateTypeField.SetValue(settings, keyValuePair.Key);
                    setValue = true;
                    break;
                }
            }

            if (!setValue)
            {
                settings.frameRate = fps;
            }

            // Now apply the window settings to all recorders 
            // and save the new global settings
            var onGlobalSettingsChanged = window.GetType().GetMethod(
                "OnGlobalSettingsChanged",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            onGlobalSettingsChanged.Invoke(window, new object[]{ });
        }
    }
}
