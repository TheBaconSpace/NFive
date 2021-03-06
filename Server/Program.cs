﻿using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations;
using System.Data.Entity.Migrations.Infrastructure;
using System.IO;
using System.Linq;
using System.Reflection;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using NFive.SDK.Server.Configuration;
using NFive.SDK.Server.Controllers;
using NFive.Server.Configuration;
using NFive.Server.Controllers;
using NFive.Server.Diagnostics;
using NFive.Server.Events;
using NFive.Server.Rpc;
using JetBrains.Annotations;
using NFive.SDK.Core.Controllers;
using NFive.SDK.Plugins;
using NFive.SDK.Server.Migrations;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.TypeInspectors;

namespace NFive.Server
{
	[UsedImplicitly]
	public class Program : BaseScript
	{
		private readonly Logger logger = new Logger();
		private readonly List<Controller> controllers = new List<Controller>();

		public Program()
		{
			// Set the AppDomain working directory to the current resource root
			Environment.CurrentDirectory = FileManager.ResolveResourcePath();

			var config = ConfigurationManager.Load<CoreConfiguration>("nfive.yml");

			ServerConfiguration.LogLevel = config.Log.Level;
			API.SetMapName(config.Display.Map);
			API.SetGameType(config.Display.Map);

			// Setup RPC handlers
			RpcManager.Configure(this.EventHandlers);

			var events = new EventManager();

			// Load core controllers
			this.controllers.Add(new DatabaseController(new Logger("Database"), events, new RpcHandler(), ConfigurationManager.Load<DatabaseConfiguration>("database.yml")));

			// Resolve dependencies
			var graph = DefinitionGraph.Load("nfive.lock");

			// Load plugins into the AppDomain
			foreach (var plugin in graph.Definitions)
			{
				this.logger.Info($"Loading {plugin.FullName}");

				// Load include files
				foreach (string includeName in plugin.Server?.Include ?? new List<string>())
				{
					string includeFile = Path.Combine("plugins", plugin.Name.Vendor, plugin.Name.Project, $"{includeName}.dll");
					if (!File.Exists(includeFile)) throw new FileNotFoundException(includeFile);

					AppDomain.CurrentDomain.Load(File.ReadAllBytes(includeFile));
				}

				// Load main files
				foreach (string mainName in plugin.Server?.Main ?? new List<string>())
				{
					string mainFile = Path.Combine("plugins", plugin.Name.Vendor, plugin.Name.Project, $"{mainName}.net.dll");
					if (!File.Exists(mainFile)) throw new FileNotFoundException(mainFile);

					var types = Assembly.LoadFrom(mainFile).GetTypes().Where(t => !t.IsAbstract && t.IsClass).ToList();

					//this.logger.Debug($"{mainName}: {types.Count} {string.Join(Environment.NewLine, types)}");

					// Find migrations
					foreach (Type migrationType in types.Where(t => t.BaseType != null && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() == typeof(MigrationConfiguration<>)))
					{
						var configuration = (DbMigrationsConfiguration)Activator.CreateInstance(migrationType);
						var migrator = new DbMigrator(configuration);

						if (!migrator.GetPendingMigrations().Any()) continue;

						if (!ServerConfiguration.AutomaticMigrations) throw new MigrationsPendingException($"Plugin {plugin.FullName} has pending migrations but automatic migrations are disabled");

						this.logger.Debug($"{mainName}: Running migrations {string.Join(", ", migrator.GetPendingMigrations())}");

						migrator.Update();
					}

					// Find controllers
					foreach (Type controllerType in types.Where(t => t.IsSubclassOf(typeof(Controller)) || t.IsSubclassOf(typeof(ConfigurableController<>))))
					{
						List<object> constructorArgs = new List<object>
						{
							new Logger($"Plugin|{plugin.Name}"),
							events,
							new RpcHandler()
						};

						// Check if controller is configurable
						if (controllerType.BaseType != null && controllerType.BaseType.IsGenericType && controllerType.BaseType.GetGenericTypeDefinition() == typeof(ConfigurableController<>))
						{
							// Get controller configuration type
							Type configurationType = controllerType.BaseType.GetGenericArguments()[0];

							var configurationObject = (ControllerConfiguration)Activator.CreateInstance(configurationType);

							if (!File.Exists(Path.Combine("config", plugin.Name.Vendor, plugin.Name.Project, $"{configurationObject.FileName}.yml")))
							{
								Directory.CreateDirectory(Path.Combine("config", plugin.Name.Vendor, plugin.Name.Project));

								var yml = new SerializerBuilder()
									.WithNamingConvention(new UnderscoredNamingConvention())
									.EmitDefaults()
									.WithTypeInspector(i => new PluginTypeInspector(i))
									.Build()
									.Serialize(Activator.CreateInstance(configurationType));

								File.WriteAllText(Path.Combine("config", plugin.Name.Vendor, plugin.Name.Project, $"{configurationObject.FileName}.yml"), yml);
							}

							// Load configuration
							object configuration = ConfigurationManager.Load(plugin.Name, configurationObject.FileName, configurationType);

							constructorArgs.Add(configuration);
						}

						// Construct controller instance
						Controller controller = (Controller)Activator.CreateInstance(controllerType, constructorArgs.ToArray());

						this.controllers.Add(controller);
					}
				}
			}

			events.Raise("serverInitialized");

			this.logger.Info($"{graph.Definitions.Count} plugins loaded, {this.controllers.Count} controller(s) created");
		}
	}

	public class PluginTypeInspector : TypeInspectorSkeleton
	{
		private readonly ITypeInspector inspector;

		public PluginTypeInspector(ITypeInspector inspector)
		{
			this.inspector = inspector;
		}

		public override IEnumerable<IPropertyDescriptor> GetProperties(Type type, object container) => this.inspector
			.GetProperties(type, container)
			.Where(p => p.CanWrite)
			.Where(p => p.Name != "file_name");
	}
}
