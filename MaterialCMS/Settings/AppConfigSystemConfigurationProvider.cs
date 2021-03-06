﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Xml.Linq;
using MaterialCMS.Helpers;
using MaterialCMS.Services;
using MaterialCMS.Settings.Events;
using StackExchange.Profiling;

namespace MaterialCMS.Settings
{
    public class AppConfigSystemConfigurationProvider : ISystemConfigurationProvider
    {
        private const string XmlHeader = "<?xml version=\"1.0\"?>\r\n";
        private const string MaterialCMSSettingsSectionName = "materialcmsSettings";
        private static readonly HashSet<string> CheckedSections = new HashSet<string>();

        public void ClearCache()
        {
        }

        public TSettings GetSystemSettings<TSettings>() where TSettings : SystemSettingsBase, new()
        {
            using (MiniProfiler.Current.Step($"Get {typeof(TSettings).FullName}"))
            {
                var settings = Activator.CreateInstance<TSettings>();

                Dictionary<PropertyInfo, object> propertiesToUpdate = new Dictionary<PropertyInfo, object>();
                foreach (var prop in typeof(TSettings).GetProperties())
                {
                    // get properties we can read and write to
                    if (!prop.CanRead || !prop.CanWrite)
                        continue;

                    //var key = typeof (TSettings).FullName + "." + prop.Name;
                    var value = GetSettingObjectByKey(prop, prop.PropertyType);
                    if (value == null)
                    {
                        // persist the default value to the config
                        propertiesToUpdate[prop] = prop.GetValue(settings);
                    }
                    else
                    {
                        //set property
                        prop.SetValue(settings, value, null);
                    }
                }
                if (propertiesToUpdate.Keys.Any())
                {
                    var config = GetConfig();
                    foreach (var key in propertiesToUpdate.Keys)
                    {
                        SetSetting(config, key, propertiesToUpdate[key]);
                    }
                    SaveConfig(config);
                }

                return settings;
            }
        }

        private static void SaveConfig(Configuration config)
        {
            config.Save(ConfigurationSaveMode.Minimal);
        }

        public void SaveSettings(SystemSettingsBase settings)
        {
            var methodInfo = GetType().GetMethods().First(x => x.Name == "SaveSettings" && x.IsGenericMethod);
            var genericMethod = methodInfo.MakeGenericMethod(settings.GetType());
            genericMethod.Invoke(this, new object[] { settings });
        }

        public void SaveSettings<TSettings>(TSettings settings) where TSettings : SystemSettingsBase, new()
        {
            var existing = GetSystemSettings<TSettings>();
            var config = GetConfig();
            foreach (var prop in typeof(TSettings).GetProperties())
            {
                // get properties we can read and write to
                if (!prop.CanRead || !prop.CanWrite)
                    continue;

                dynamic value = prop.GetValue(settings, null);
                SetSetting(config, prop, value ?? "");
            }
            SaveConfig(config);

            EventContext.Instance.Publish<IOnSavingSystemSettings<TSettings>, OnSavingSystemSettingsArgs<TSettings>>(
                new OnSavingSystemSettingsArgs<TSettings>(settings, existing));
        }

        public List<SystemSettingsBase> GetAllSystemSettings()
        {
            var methodInfo = GetType().GetMethodExt("GetSystemSettings");

            return TypeHelper.GetAllConcreteTypesAssignableFrom<SystemSettingsBase>()
                .Select(type => methodInfo.MakeGenericMethod(type).Invoke(this, new object[] { }))
                .OfType<SystemSettingsBase>().ToList();
        }

        protected virtual Configuration GetConfig()
        {
            var config = WebConfigurationManager.OpenWebConfiguration("~");
            return config;
        }

        private void EnsureMaterialCMSSettingsSourceExists(Configuration config)
        {
            EnsureSourceExists(config, MaterialCMSSettingsSectionName);
        }

        private void EnsureConnectionStringsSourceExists(Configuration config)
        {
            EnsureSourceExists(config, "connectionStrings");
        }

        private static void EnsureSourceExists(Configuration config, string sectionName)
        {
            using (MiniProfiler.Current.Step($"Ensuring {sectionName} exists"))
            {
                // Adding this checking in to prevent repeated parsing/reading of the config file
                if (CheckedSections.Contains(sectionName))
                    return;
                // add now to ensure it's not looked at excessively
                CheckedSections.Add(sectionName);
                var xDocument = XDocument.Parse(File.ReadAllText(config.FilePath));
                var section = xDocument.Descendants(sectionName).FirstOrDefault();
                if (section == null)
                    return;
                var configSource = section.Attribute("configSource");
                if (configSource == null || string.IsNullOrWhiteSpace(configSource.Value))
                    return;
                var path = Path.Combine(HostingEnvironment.ApplicationPhysicalPath, configSource.Value);
                if (!File.Exists(path))
                    File.WriteAllText(path, XmlHeader + string.Format("<{0}></{0}>", sectionName));
            }
        }

        /// <summary>
        ///     Set setting value
        /// </summary>
        /// <typeparam name="T">Type</typeparam>
        /// <param name="configuration">Config to write to</param>
        /// <param name="property">property being persisted</param>
        /// <param name="value">Value</param>
        protected virtual void SetSetting<T>(Configuration configuration, PropertyInfo property, T value)
        {
            var valueStr = value.To<string>();
            var result = IsConnectionString(property);
            if (result.IsConnectionString)
            {
                configuration.ConnectionStrings.ConnectionStrings.Remove(result.Name);
                configuration.ConnectionStrings.ConnectionStrings.Add(new ConnectionStringSettings(result.Name, valueStr));
                return;
            }
            var mrCMSSettings = GetMaterialCMSSettingsFromConfig(configuration);
            var key = GetPropertyKey(property);
            if (key == null)
                throw new ArgumentNullException("key");
            key = key.Trim().ToLowerInvariant();

            mrCMSSettings.Remove(key);
            mrCMSSettings.Add(key, valueStr);
        }

        /// <summary>
        ///     Get setting value by key
        /// </summary>
        /// <param name="configuration">Config to write to</param>
        /// <param name="property">property being retrieved</param>
        /// <param name="type">value type</param>
        /// <returns>Setting value</returns>
        protected virtual object GetSettingObjectByKey(PropertyInfo property, Type type)
        {
            var result = IsConnectionString(property);
            if (result.IsConnectionString)
            {
                return GetConnectionString(result.Name);
            }
            var key = GetPropertyKey(property);
            if (string.IsNullOrWhiteSpace(key))
                return null;

            key = key.Trim().ToLowerInvariant();
            var settings = GetMaterialCMSSettings();
            if (settings.AllKeys.Contains(key))
            {
                var setting = settings[key];
                if (setting != null)
                    return setting.Value.To(type);
            }

            return null;
        }

        private KeyValueConfigurationCollection GetMaterialCMSSettings()
        {
            try
            {
                return (WebConfigurationManager.GetSection(MaterialCMSSettingsSectionName) as MaterialCMSSettingsSection).Settings;
            }
            catch
            {
                // we do this to create the config if it doesn't exist
                var config = GetConfig();
                EnsureMaterialCMSSettingsSourceExists(config);
                return GetMaterialCMSSettingsFromConfig(config);
            }
        }
        private static KeyValueConfigurationCollection GetMaterialCMSSettingsFromConfig(Configuration config)
        {
            return (config.GetSection(MaterialCMSSettingsSectionName) as MaterialCMSSettingsSection).Settings;
        }

        private string GetPropertyKey(PropertyInfo property)
        {
            var attribute = property.GetCustomAttribute<AppSettingNameAttribute>();
            if (attribute == null)
                return property.ReflectedType.FullName + "." + property.Name;
            return attribute.Name;
        }

        private object GetConnectionString(string name)
        {
            try
            {
                return GetConnectionStringFromManager(name);
            }
            catch 
            {
                var config = GetConfig();
                EnsureConnectionStringsSourceExists(config);
                return GetConnectionStringFromManager(name);
            }
        }

        private static object GetConnectionStringFromManager(string name)
        {
            var connectionString = WebConfigurationManager.ConnectionStrings[name];
            return connectionString != null ? connectionString.ConnectionString : null;
        }

        private IsConnectionStringResult IsConnectionString(PropertyInfo property)
        {
            var attribute = property.GetCustomAttribute<ConnectionStringAttribute>();
            if (attribute == null)
                return new IsConnectionStringResult();
            return new IsConnectionStringResult
            {
                IsConnectionString = true,
                Name = attribute.Name
            };
        }

        public class IsConnectionStringResult
        {
            public bool IsConnectionString { get; set; }
            public string Name { get; set; }
        }

        public void Initialize(DatabaseSettings settings)
        {
            var config = GetConfig();
            EnsureConnectionStringsSourceExists(config);
            EnsureMaterialCMSSettingsSourceExists(config);
            config = GetConfig();

            foreach (var prop in typeof(DatabaseSettings).GetProperties())
            {
                // get properties we can read and write to
                if (!prop.CanRead || !prop.CanWrite)
                    continue;

                dynamic value = prop.GetValue(settings, null);
                SetSetting(config, prop, value ?? "");
            }
            SaveConfig(config);
        }
    }
}