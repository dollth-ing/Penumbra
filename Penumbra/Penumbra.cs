using System;
using Dalamud.Game.Command;
using Dalamud.Logging;
using Dalamud.Plugin;
using EmbedIO;
using EmbedIO.WebApi;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using Penumbra.Api;
using Penumbra.GameData.Enums;
using Penumbra.Interop;
using Penumbra.UI;
using Penumbra.Util;
using Penumbra.Collections;
using Penumbra.Interop.Loader;
using Penumbra.Interop.Resolver;
using Penumbra.Mods;

namespace Penumbra;

public class Penumbra : IDalamudPlugin
{
    public string Name
        => "Penumbra";

    public string PluginDebugTitleStr
        => "Penumbra - Debug Build";

    private const string CommandName = "/penumbra";

    public static Configuration Config { get; private set; } = null!;

    public static ResidentResourceManager ResidentResources { get; private set; } = null!;
    public static CharacterUtility CharacterUtility { get; private set; } = null!;
    public static MetaFileManager MetaFileManager { get; private set; } = null!;

    public static Mod.Manager ModManager { get; private set; } = null!;
    public static ModCollection.Manager CollectionManager { get; private set; } = null!;

    public static ResourceLoader ResourceLoader { get; set; } = null!;
    public ResourceLogger ResourceLogger { get; }

    public PathResolver PathResolver { get; }
    public SettingsInterface SettingsInterface { get; }
    public MusicManager MusicManager { get; }
    public ObjectReloader ObjectReloader { get; }

    public PenumbraApi Api { get; }
    public PenumbraIpc Ipc { get; }

    private WebServer? _webServer;

    public Penumbra( DalamudPluginInterface pluginInterface )
    {
        Dalamud.Initialize( pluginInterface );
        GameData.GameData.GetIdentifier( Dalamud.GameData, Dalamud.ClientState.ClientLanguage );
        Config = Configuration.Load();

        MusicManager = new MusicManager();
        if( Config.DisableSoundStreaming )
        {
            MusicManager.DisableStreaming();
        }

        ResidentResources = new ResidentResourceManager();
        CharacterUtility  = new CharacterUtility();
        MetaFileManager    = new MetaFileManager();
        ResourceLoader    = new ResourceLoader( this );
        ResourceLogger    = new ResourceLogger( ResourceLoader );
        ModManager        = new Mod.Manager();
        ModManager.DiscoverMods();
        CollectionManager = new ModCollection.Manager( ModManager );
        ObjectReloader    = new ObjectReloader();
        PathResolver      = new PathResolver( ResourceLoader );

        Dalamud.Commands.AddHandler( CommandName, new CommandInfo( OnCommand )
        {
            HelpMessage = "/penumbra - toggle ui\n/penumbra reload - reload mod file lists & discover any new mods",
        } );

        if( Config.DebugMode )
        {
            ResourceLoader.EnableDebug();
        }

        ResidentResources.Reload();

        Api = new PenumbraApi( this );
        Ipc = new PenumbraIpc( pluginInterface, Api );
        SubscribeItemLinks();

        SettingsInterface = new SettingsInterface( this );

        if( Config.EnableHttpApi )
        {
            CreateWebServer();
        }

        ResourceLoader.EnableHooks();
        if( Config.EnableMods )
        {
            ResourceLoader.EnableReplacements();
        }

        if( Config.DebugMode )
        {
            ResourceLoader.EnableDebug();
        }

        if( Config.EnableFullResourceLogging )
        {
            ResourceLoader.EnableFullLogging();
        }

        if( CollectionManager.HasCharacterCollections )
        {
            PathResolver.Enable();
        }

        ResidentResources.Reload();
        //var c = ModCollection2.LoadFromFile( new FileInfo(@"C:\Users\Ozy\AppData\Roaming\XIVLauncher\pluginConfigs\Penumbra\collections\Rayla.json"),
        //    out var inheritance );
        //c?.Save();
    }

    public bool Enable()
    {
        if( Config.EnableMods )
        {
            return false;
        }

        Config.EnableMods = true;
        ResourceLoader.EnableReplacements();
        ResidentResources.Reload();

        Config.Save();
        ObjectReloader.RedrawAll( RedrawType.Redraw );
        return true;
    }

    public bool Disable()
    {
        if( !Config.EnableMods )
        {
            return false;
        }

        Config.EnableMods = false;
        ResourceLoader.DisableReplacements();
        ResidentResources.Reload();

        Config.Save();
        ObjectReloader.RedrawAll( RedrawType.Redraw );
        return true;
    }

    public bool SetEnabled( bool enabled )
        => enabled ? Enable() : Disable();

    private void SubscribeItemLinks()
    {
        Api.ChangedItemTooltip += it =>
        {
            if( it is Item )
            {
                ImGui.Text( "Left Click to create an item link in chat." );
            }
        };
        Api.ChangedItemClicked += ( button, it ) =>
        {
            if( button == MouseButton.Left && it is Item item )
            {
                ChatUtil.LinkItem( item );
            }
        };
    }

    public void CreateWebServer()
    {
        const string prefix = "http://localhost:42069/";

        ShutdownWebServer();

        _webServer = new WebServer( o => o
               .WithUrlPrefix( prefix )
               .WithMode( HttpListenerMode.EmbedIO ) )
           .WithCors( prefix )
           .WithWebApi( "/api", m => m
               .WithController( () => new ModsController( this ) )
               .WithController( () => new RedrawController( this ) ) );

        _webServer.StateChanged += ( _, e ) => PluginLog.Information( $"WebServer New State - {e.NewState}" );

        _webServer.RunAsync();
    }

    public void ShutdownWebServer()
    {
        _webServer?.Dispose();
        _webServer = null;
    }

    public void Dispose()
    {
        Ipc.Dispose();
        Api.Dispose();
        SettingsInterface.Dispose();
        ObjectReloader.Dispose();
        CollectionManager.Dispose();

        Dalamud.Commands.RemoveHandler( CommandName );

        PathResolver.Dispose();
        ResourceLogger.Dispose();
        MetaFileManager.Dispose();
        ResourceLoader.Dispose();
        CharacterUtility.Dispose();

        ShutdownWebServer();
    }

    public bool SetCollection( string type, string collectionName )
    {
        type           = type.ToLowerInvariant();
        collectionName = collectionName.ToLowerInvariant();

        var collection = string.Equals( collectionName, ModCollection.Empty.Name, StringComparison.InvariantCultureIgnoreCase )
            ? ModCollection.Empty
            : CollectionManager[ collectionName ];
        if( collection == null )
        {
            Dalamud.Chat.Print( $"The collection {collection} does not exist." );
            return false;
        }

        switch( type )
        {
            case "default":
                if( collection == CollectionManager.Default )
                {
                    Dalamud.Chat.Print( $"{collection.Name} already is the default collection." );
                    return false;
                }

                CollectionManager.SetCollection( collection, ModCollection.Type.Default );
                Dalamud.Chat.Print( $"Set {collection.Name} as default collection." );
                SettingsInterface.ResetDefaultCollection();
                return true;
            default:
                Dalamud.Chat.Print(
                    "Second command argument is not default, the correct command format is: /penumbra collection default <collectionName>" );
                return false;
        }
    }

    private void OnCommand( string command, string rawArgs )
    {
        const string modsEnabled  = "Your mods have now been enabled.";
        const string modsDisabled = "Your mods have now been disabled.";

        var args = rawArgs.Split( new[] { ' ' }, 2 );
        if( args.Length > 0 && args[ 0 ].Length > 0 )
        {
            switch( args[ 0 ] )
            {
                case "reload":
                {
                    ModManager.DiscoverMods();
                    Dalamud.Chat.Print(
                        $"Reloaded Penumbra mods. You have {ModManager.Mods.Count} mods."
                    );
                    break;
                }
                case "redraw":
                {
                    if( args.Length > 1 )
                    {
                        ObjectReloader.RedrawObject( args[ 1 ], RedrawType.Redraw );
                    }
                    else
                    {
                        ObjectReloader.RedrawAll( RedrawType.Redraw );
                    }

                    break;
                }
                case "debug":
                {
                    SettingsInterface.MakeDebugTabVisible();
                    break;
                }
                case "enable":
                {
                    Dalamud.Chat.Print( Enable()
                        ? "Your mods are already enabled. To disable your mods, please run the following command instead: /penumbra disable"
                        : modsEnabled );
                    break;
                }
                case "disable":
                {
                    Dalamud.Chat.Print( Disable()
                        ? "Your mods are already disabled. To enable your mods, please run the following command instead: /penumbra enable"
                        : modsDisabled );
                    break;
                }
                case "toggle":
                {
                    SetEnabled( !Config.EnableMods );
                    Dalamud.Chat.Print( Config.EnableMods
                        ? modsEnabled
                        : modsDisabled );
                    break;
                }
                case "collection":
                {
                    if( args.Length == 2 )
                    {
                        args = args[ 1 ].Split( new[] { ' ' }, 2 );
                        if( args.Length == 2 )
                        {
                            SetCollection( args[ 0 ], args[ 1 ] );
                        }
                    }
                    else
                    {
                        Dalamud.Chat.Print( "Missing arguments, the correct command format is:"
                          + " /penumbra collection {default|forced} <collectionName>" );
                    }

                    break;
                }
            }

            return;
        }

        SettingsInterface.FlipVisibility();
    }
}