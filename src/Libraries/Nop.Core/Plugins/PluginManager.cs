﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Nop.Core.ComponentModel;
using Nop.Core.Configuration;
using Nop.Core.Infrastructure;

namespace Nop.Core.Plugins
{
    /// <summary>
    /// Represents the plugin manager
    /// </summary>
    public partial class PluginManager
    {
        #region Fields

        private static readonly INopFileProvider _fileProvider;
        private static NopConfig _config;
        private static ApplicationPartManager _applicationPartManager;

        private static readonly List<string> _baseAppLibraries;
        private static readonly Dictionary<string, PluginLoadedAssemblyInfo> _loadedAssemblies = new Dictionary<string, PluginLoadedAssemblyInfo>();

        private static readonly ReaderWriterLockSlim Locker = new ReaderWriterLockSlim();

        #endregion

        #region Ctor

        static PluginManager()
        {
            //we use the default file provider, since the DI isn't initialized yet
            _fileProvider = CommonHelper.DefaultFileProvider;

            _baseAppLibraries = new List<string>();

            //get all libraries from /bin/{version}/ directory
            _baseAppLibraries.AddRange(_fileProvider.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll")
                .Select(fileName => _fileProvider.GetFileName(fileName)));

            //get all libraries from base site directory
            if (!AppDomain.CurrentDomain.BaseDirectory.Equals(Environment.CurrentDirectory, StringComparison.InvariantCultureIgnoreCase))
            {
                _baseAppLibraries.AddRange(_fileProvider.GetFiles(Environment.CurrentDirectory, "*.dll")
                    .Select(fileName => _fileProvider.GetFileName(fileName)));
            }

            //get all libraries from refs directory
            var refsPathName = _fileProvider.Combine(Environment.CurrentDirectory, NopPluginDefaults.RefsPathName);
            if (_fileProvider.DirectoryExists(refsPathName))
            {
                _baseAppLibraries.AddRange(_fileProvider.GetFiles(refsPathName, "*.dll")
                    .Select(fileName => _fileProvider.GetFileName(fileName)));
            }

            //load plugins info (names of already installed, going to be installed, going to be uninstalled and going to be deleted plugins)
            PluginsInfo = PluginsInfo.LoadPluginInfo(_fileProvider);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a collection of plugin descriptors of all deployed plugins
        /// </summary>
        public static IEnumerable<PluginDescriptor> PluginDescriptors { get; set; }

        /// <summary>
        /// Gets access to information about plugins
        /// </summary>
        public static PluginsInfo PluginsInfo { get; }

        #endregion

        #region Utilities

        /// <summary>
        /// Get list of description files-plugin descriptors pairs
        /// </summary>
        /// <param name="directoryName">Plugin directory name</param>
        /// <returns>Original and parsed description files</returns>
        private static IList<(string DescriptionFile, PluginDescriptor PluginDescriptor)> GetDescriptionFilesAndDescriptors(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
                throw new ArgumentNullException(nameof(directoryName));

            var result = new List<(string DescriptionFile, PluginDescriptor PluginDescriptor)>();

            //try to find description files in the plugin directory
            var files = _fileProvider.GetFiles(directoryName, NopPluginDefaults.DescriptionFileName, false);

            //populate result list
            foreach (var descriptionFile in files)
            {
                //skip files that are not in the plugin directory
                if (!IsPluginDirectory(_fileProvider.GetDirectoryName(descriptionFile)))
                    continue;

                //load plugin descriptor from the file
                var text = _fileProvider.ReadAllText(descriptionFile, Encoding.UTF8);
                var pluginDescriptor = PluginDescriptor.GetPluginDescriptorFromText(text);

                result.Add((descriptionFile, pluginDescriptor));
            }

            //sort list by display order. NOTE: Lowest DisplayOrder will be first i.e 0 , 1, 1, 1, 5, 10
            //it's required: https://www.nopcommerce.com/boards/t/17455/load-plugins-based-on-their-displayorder-on-startup.aspx
            result = result.OrderBy(item => item.PluginDescriptor.DisplayOrder).ToList();

            return result;
        }

        /// <summary>
        /// Check whether the assembly is already loaded
        /// </summary>
        /// <param name="filePath">Assembly file path</param>
        /// <param name="pluginName">Plugin system name</param>
        /// <returns>Result of check</returns>
        private static bool IsAlreadyLoaded(string filePath, string pluginName)
        {
            //ignore already loaded libraries
            //(we do it because not all libraries are loaded immediately after application start)
            var fileName = _fileProvider.GetFileName(filePath);
            if (_baseAppLibraries.Any(library => library.Equals(fileName, StringComparison.InvariantCultureIgnoreCase)))
                return true;

            try
            {
                //get filename without extension
                var fileNameWithoutExtension = _fileProvider.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(fileNameWithoutExtension))
                    throw new Exception($"Cannot get file extension for {fileName}");

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    //compare assemblies by filenames
                    var assemblyName = assembly.FullName.Split(',').FirstOrDefault();
                    if (!fileNameWithoutExtension.Equals(assemblyName, StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    //already loaded assembly found
                    if (!_loadedAssemblies.ContainsKey(assemblyName))
                    {
                        //add it to the list to find collisions later
                        _loadedAssemblies.Add(assemblyName, new PluginLoadedAssemblyInfo(assemblyName, assembly.FullName));
                    }

                    //set assembly name and plugin name for further using
                    _loadedAssemblies[assemblyName].References.Add((pluginName, AssemblyName.GetAssemblyName(filePath).FullName));

                    return true;
                }
            }
            catch { }

            //nothing found
            return false;
        }

        /// <summary>
        /// Checks whether need to deploy a plugin
        /// </summary>
        /// <param name="pluginName">Plugin system name</param>
        /// <returns>Result of check</returns>
        private static bool NeedToDeploy(string pluginName)
        {
            //first, need to deploy if plugin is already installed
            var needToDeploy = PluginsInfo.InstalledPluginNames.Contains(pluginName);

            //also, deploy if the plugin is only going to be installed now
            needToDeploy = needToDeploy || PluginsInfo.PluginNamesToInstall.Any(item => item.SystemName.Equals(pluginName));

            //finally, exclude from deploying the plugin that is going to be deleted
            needToDeploy = needToDeploy && !PluginsInfo.PluginNamesToDelete.Contains(pluginName);

            return needToDeploy;
        }

        /// <summary>
        /// Perform file deploy and return loaded assembly
        /// </summary>
        /// <param name="assemblyFile">Path to the plugin assembly file</param>
        /// <param name="shadowCopyDirectory">Path to the shadow copy directory</param>
        /// <returns>Assembly</returns>
        private static Assembly PerformFileDeploy(string assemblyFile, string shadowCopyDirectory)
        {
            //ensure for proper directory structure
            if (string.IsNullOrEmpty(assemblyFile) || string.IsNullOrEmpty(_fileProvider.GetParentDirectory(assemblyFile)))
            {
                throw new InvalidOperationException($"The plugin directory for the {_fileProvider.GetFileName(assemblyFile)} file " +
                    $"exists in a directory outside of the allowed nopCommerce directory hierarchy");
            }

            //whether to copy plugins assemblies to the bin directory, if not load assembly from the original file
            if (!_config.UsePluginsShadowCopy)
                return LoadPluginAssembly(assemblyFile);

            //or try to shadow copy first
            _fileProvider.CreateDirectory(shadowCopyDirectory);
            var shadowCopiedFile = ShadowCopyFile(assemblyFile, shadowCopyDirectory);

            Assembly shadowCopiedAssembly = null;
            try
            {
                //and load assembly from the shadow copy
                shadowCopiedAssembly = LoadPluginAssembly(shadowCopiedFile);
            }
            catch (FileLoadException)
            {
                //suppress exceptions for "locked" assemblies, try load them from another directory
                if (!_config.CopyLockedPluginAssembilesToSubdirectoriesOnStartup ||
                    !shadowCopyDirectory.Equals(_fileProvider.MapPath(NopPluginDefaults.ShadowCopyPath)))
                {
                    throw;
                }
            }

            //shadow copy assembly wasn't loaded for some reason, so trying to load again from the reserve directory
            if (shadowCopiedAssembly == null)
            {
                var reserveDirectory = _fileProvider.Combine(_fileProvider.MapPath(NopPluginDefaults.ShadowCopyPath),
                    $"{NopPluginDefaults.ReserveShadowCopyPathName}{DateTime.Now.ToFileTimeUtc()}");
                return PerformFileDeploy(assemblyFile, reserveDirectory);
            }

            return shadowCopiedAssembly;
        }

        /// <summary>
        /// Load and register the plugin assembly
        /// </summary>
        /// <param name="assemblyFile">Path to the plugin assembly file</param>
        /// <returns>Assembly</returns>
        private static Assembly LoadPluginAssembly(string assemblyFile)
        {
            //try to load a plugin assembly
            Assembly pluginAssembly;
            try
            {
                pluginAssembly = Assembly.LoadFrom(assemblyFile);
            }
            catch (FileLoadException)
            {
                if (_config.UseUnsafeLoadAssembly)
                {
                    //if an application has been copied from the web, it is flagged by Windows as being a web application,
                    //even if it resides on the local computer.You can change that designation by changing the file properties,
                    //or you can use the<loadFromRemoteSources> element to grant the assembly full trust.As an alternative,
                    //you can use the UnsafeLoadFrom method to load a local assembly that the operating system has flagged as
                    //having been loaded from the web.
                    //see http://go.microsoft.com/fwlink/?LinkId=155569 for more information.
                    pluginAssembly = Assembly.UnsafeLoadFrom(assemblyFile);
                }
                else throw;
            }

            //register the plugin definition
            _applicationPartManager.ApplicationParts.Add(new AssemblyPart(pluginAssembly));

            return pluginAssembly;
        }

        /// <summary>
        /// Copy the plugin assembly file to the shadow copy directory
        /// </summary>
        /// <param name="assemblyFile">Path to the plugin assembly file</param>
        /// <param name="shadowCopyDirectory">Path to the shadow copy directory</param>
        /// <returns>Path to the shadow copied file</returns>
        private static string ShadowCopyFile(string assemblyFile, string shadowCopyDirectory)
        {
            //get path to the new shadow copied file
            var shadowCopiedFile = _fileProvider.Combine(shadowCopyDirectory, _fileProvider.GetFileName(assemblyFile));

            //check if a shadow copied file already exists and if it does
            if (_fileProvider.FileExists(shadowCopiedFile))
            {
                //it exists, then check if it's updated (compare creation time of files)
                var areFilesIdentical = _fileProvider.GetCreationTime(shadowCopiedFile).ToUniversalTime().Ticks >=
                    _fileProvider.GetCreationTime(assemblyFile).ToUniversalTime().Ticks;
                if (areFilesIdentical)
                {
                    //no need to copy again
                    return shadowCopiedFile;
                }
                else
                {
                    //file already exists but passed file is more updated, so delete an existing file and copy again
                    //More info: https://www.nopcommerce.com/boards/t/11511/access-error-nopplugindiscountrulesbillingcountrydll.aspx?p=4#60838
                    _fileProvider.DeleteFile(shadowCopiedFile);
                }
            }

            //try to shadow copy
            try
            {
                _fileProvider.FileCopy(assemblyFile, shadowCopiedFile, true);
            }
            catch (IOException)
            {
                //this occurs when the files are locked,
                //for some reason devenv locks plugin files some times and for another crazy reason you are allowed to rename them
                //which releases the lock, so that it what we are doing here, once it's renamed, we can re-shadow copy
                try
                {
                    var oldFile = $"{shadowCopiedFile}{Guid.NewGuid().ToString("N")}.old";
                    _fileProvider.FileMove(shadowCopiedFile, oldFile);
                }
                catch (IOException exception)
                {
                    throw new IOException($"{shadowCopiedFile} rename failed, cannot initialize plugin", exception);
                }

                //or retry the shadow copy
                _fileProvider.FileCopy(assemblyFile, shadowCopiedFile, true);
            }

            return shadowCopiedFile;
        }

        /// <summary>
        /// Check whether the directory is a plugin directory
        /// </summary>
        /// <param name="directoryName">Directory name</param>
        /// <returns>Result of check</returns>
        private static bool IsPluginDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
                return false;

            //get parent directory
            var parent = _fileProvider.GetParentDirectory(directoryName);
            if (string.IsNullOrEmpty(parent))
                return false;

            //directory is directly in plugins directory
            if (!_fileProvider.GetDirectoryNameOnly(parent).Equals(NopPluginDefaults.PathName, StringComparison.InvariantCultureIgnoreCase))
                return false;

            return true;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Initialize
        /// </summary>
        /// <param name="applicationPartManager">Application part manager</param>
        /// <param name="config">Config</param>
        public static void Initialize(ApplicationPartManager applicationPartManager, NopConfig config)
        {
            _applicationPartManager = applicationPartManager
                ?? throw new ArgumentNullException(nameof(applicationPartManager));

            _config = config
                ?? throw new ArgumentNullException(nameof(config));

            //perform with locked access to resources
            using (new WriteLockDisposable(Locker))
            {
                var pluginDescriptors = new List<PluginDescriptor>();
                var incompatiblePlugins = new List<string>();

                try
                {
                    //ensure plugins directory and directory for shadow copies are created
                    var pluginsDirectory = _fileProvider.MapPath(NopPluginDefaults.Path);
                    _fileProvider.CreateDirectory(pluginsDirectory);

                    var shadowCopyDirectory = _fileProvider.MapPath(NopPluginDefaults.ShadowCopyPath);
                    _fileProvider.CreateDirectory(shadowCopyDirectory);

                    //get list of all files in bin directory
                    var binFiles = _fileProvider.GetFiles(shadowCopyDirectory, "*", false);

                    //whether to clear shadow copied files
                    if (_config.ClearPluginShadowDirectoryOnStartup)
                    {
                        //skip placeholder files
                        var placeholderFileNames = new List<string> { "placeholder.txt", "index.htm" };
                        binFiles = binFiles.Where(file =>
                        {
                            var fileName = _fileProvider.GetFileName(file);
                            return !placeholderFileNames.Any(placeholderFileName => placeholderFileName
                                .Equals(fileName, StringComparison.InvariantCultureIgnoreCase));
                        }).ToArray();

                        //clear out directory
                        foreach (var file in binFiles)
                        {
                            try
                            {
                                _fileProvider.DeleteFile(file);
                            }
                            catch { }
                        }

                        //delete all reserve directories
                        var reserveDirectories = _fileProvider
                            .GetDirectories(shadowCopyDirectory, NopPluginDefaults.ReserveShadowCopyPathNamePattern);
                        foreach (var directory in reserveDirectories)
                        {
                            try
                            {
                                _fileProvider.DeleteDirectory(directory);
                            }
                            catch { }
                        }
                    }

                    //load plugin descriptors from the plugin directory
                    foreach (var item in GetDescriptionFilesAndDescriptors(pluginsDirectory))
                    {
                        var descriptionFile = item.DescriptionFile;
                        var pluginDescriptor = item.PluginDescriptor;

                        //ensure that plugin is compatible with the current version
                        if (!pluginDescriptor.SupportedVersions.Contains(NopVersion.CurrentVersion, StringComparer.InvariantCultureIgnoreCase))
                        {
                            incompatiblePlugins.Add(pluginDescriptor.SystemName);
                            continue;
                        }

                        //some more validation
                        if (string.IsNullOrEmpty(pluginDescriptor.SystemName?.Trim()))
                        {
                            throw new Exception($"A plugin '{descriptionFile}' has no system name." +
                                $" Try assigning the plugin a unique name and recompiling.");
                        }

                        if (pluginDescriptors.Contains(pluginDescriptor))
                            throw new Exception($"A plugin with '{pluginDescriptor.SystemName}' system name is already defined");

                        //set 'Installed' property
                        pluginDescriptor.Installed = PluginsInfo.InstalledPluginNames
                            .Any(pluginName => pluginName.Equals(pluginDescriptor.SystemName, StringComparison.InvariantCultureIgnoreCase));

                        try
                        {
                            //try to get plugin directory
                            var pluginDirectory = _fileProvider.GetDirectoryName(descriptionFile);
                            if (string.IsNullOrEmpty(pluginDirectory))
                            {
                                throw new Exception($"Directory cannot be resolved" +
                                    $" for '{_fileProvider.GetFileName(descriptionFile)}' description file");
                            }

                            //get list of all library files in thr plugin directory (not in the bin one)
                            var pluginFiles = _fileProvider.GetFiles(pluginDirectory, "*.dll", false)
                                .Where(file => !binFiles.Contains(file) && IsPluginDirectory(_fileProvider.GetDirectoryName(file)))
                                .ToList();

                            //try to find a main plugin assembly file
                            var mainPluginFile = pluginFiles.FirstOrDefault(file =>
                            {
                                var fileName = _fileProvider.GetFileName(file);
                                return fileName.Equals(pluginDescriptor.AssemblyFileName, StringComparison.InvariantCultureIgnoreCase);
                            });

                            //file with the specified name not found
                            if (mainPluginFile == null)
                            {
                                //so plugin is incompatible
                                incompatiblePlugins.Add(pluginDescriptor.SystemName);
                                continue;
                            }

                            //if it's found, set it as original assembly file
                            pluginDescriptor.OriginalAssemblyFile = mainPluginFile;

                            //whether plugin need to be deployed
                            if (NeedToDeploy(pluginDescriptor.SystemName))
                            {
                                //try to deploy main plugin assembly 
                                pluginDescriptor.ReferencedAssembly = PerformFileDeploy(mainPluginFile, shadowCopyDirectory);

                                //and then deploy all other referenced assemblies
                                var filesToDeploy = pluginFiles.Where(file =>
                                    !_fileProvider.GetFileName(file).Equals(_fileProvider.GetFileName(mainPluginFile)) &&
                                    !IsAlreadyLoaded(file, pluginDescriptor.SystemName)).ToList();
                                foreach (var file in filesToDeploy)
                                {
                                    PerformFileDeploy(file, shadowCopyDirectory);
                                }

                                //determine a plugin type (only one plugin per assembly is allowed)
                                var pluginType = pluginDescriptor.ReferencedAssembly.GetTypes().FirstOrDefault(type =>
                                    typeof(IPlugin).IsAssignableFrom(type) && !type.IsInterface && type.IsClass && !type.IsAbstract);
                                if (pluginType != default(Type))
                                    pluginDescriptor.PluginType = pluginType;
                            }

                            //skip descriptor of plugin that is going to be deleted
                            if (PluginsInfo.PluginNamesToDelete.Contains(pluginDescriptor.SystemName))
                                continue;

                            //mark plugin as successfully deployed
                            pluginDescriptors.Add(pluginDescriptor);
                        }
                        catch (ReflectionTypeLoadException exception)
                        {
                            //get all loader exceptions
                            var error = exception.LoaderExceptions.Aggregate($"Plugin '{pluginDescriptor.FriendlyName}'. ",
                                (message, nextMessage) => $"{message}{nextMessage.Message}{Environment.NewLine}");

                            throw new Exception(error, exception);
                        }
                        catch (Exception exception)
                        {
                            //add a plugin name, this way we can easily identify a problematic plugin
                            throw new Exception($"Plugin '{pluginDescriptor.FriendlyName}'. {exception.Message}", exception);
                        }
                    }
                }
                catch (Exception exception)
                {
                    //throw full exception
                    var message = string.Empty;
                    for (var inner = exception; inner != null; inner = inner.InnerException)
                        message = $"{message}{inner.Message}{Environment.NewLine}";

                    throw new Exception(message, exception);
                }

                //set plugin descriptor list
                PluginDescriptors = pluginDescriptors;

                //set additional information to plugins info object and save changes
                PluginsInfo.IncompatiblePlugins = incompatiblePlugins;
                PluginsInfo.AssemblyLoadedCollision = _loadedAssemblies.Select(item => item.Value)
                    .Where(loadedAssemblyInfo => loadedAssemblyInfo.Collisions.Any()).ToList();
            }
        }

        #endregion
    }
}