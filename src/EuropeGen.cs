using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using System.Linq;
using Vintagestory.API.Util;

namespace EuropeGen
{
    // Górska pokrywa jest zwykłym śniegiem, ale nie znika od historycznej
    // ciepłej próbki regionu, gdy w tym konkretnym miejscu nadal jest mróz.
    public class MountainSnow : BlockSnow
    {
        public override Block GetSnowCoveredVariant(BlockPos pos,float snowLevel)
        {
            var climate=api.World.BlockAccessor.GetClimateAt(pos,EnumGetClimateMode.NowValues);
            if(climate!=null && climate.Temperature<=0)return this;
            return base.GetSnowCoveredVariant(pos,snowLevel);
        }
    }

    public class EuropeGenMod : ModSystem
    {
        const string Version = "0.1.24";
        const double OasisRadius = 48;
        const bool AutoFitWorld = true;
        const double WorldCoverage = 0.92;
        const int SnowAboveSea = 65;
        const float SnowClimateTemperature = -8;
        const double LowlandRiseFactor = 0.5;
        double lowlandRiseFactor = LowlandRiseFactor;
        const int ExtraSeaDepth = 4;
        const int RockStartAboveSea = 15;
        const int RockPeakAboveSea = 45;
        const int RockSlopeThreshold = 4;
        const int SoilLayerThickness = 4;
        const int SurfaceFlowLevel = 7;
        const double MassifLiftFraction = 0.55;
        const int MountainRoofMargin = 12;
        ICoreServerAPI sapi;
        bool loggedFirstColumn;
        bool storyWasSaved;

        // dane z europe.bin
        ushort[] hmap;   // wysokość powierzchni
        ushort[] wmap;   // poziom wody (0 = brak)
        byte[] rmap;     // indeks skały
        byte[] riverFlow; // 0: stojąca, 1..8: N, NE, E, SE, S, SW, W, NW
        byte[] massif, bankMask, flowingMask, uplift;
        float[] riverSurface;
        ushort[] oasisDistance;
        int[,] waterLevelIds;
        int worldHeightLimit = 256;
        int mapW, mapH, spawnPx, spawnPz, hdrSea;
        double bpp;
        int idSnow, idSnowBlock, idGlacierIce;
        List<BiomeZone> biomes = new();
        List<ResourceZone> resourceZones = new();
        HashSet<string> controlledDeposits = new(StringComparer.OrdinalIgnoreCase);
        public class ResourceZone
        {
            public string Name;
            public double West, South, East, North;
            public string[] Deposits;
            public double Factor = 1;
            public string Rock;
        }
        public class BiomeZone
        {
            public string Name;
            public double West, South, East, North;
            public float Temperature;
            public int Rain, Forest, Shrubs;
        }

        Dictionary<string, double[]> places = new();

        // Lewy górny róg mapy w blokach. Mapa jest wyśrodkowana na (0,0).
        int offsetX, offsetZ;
        const int CS = 32; // rozmiar chunka

        int seaLevel, shift;
        int idAir = 0, idBedrock, idSoil, idSoilBare, idWater, idWaterfall;
        int[] flowingWaterIds;
        int[] rockIds, sandIds;

        // musi być zgodne z ROCKS w prepare_heightmap.py
        static readonly string[] RockCodes =
            { "granite", "limestone", "sandstone", "basalt", "andesite", "chalk", "claystone", "shale" };

        public override bool ShouldLoad(EnumAppSide side) => true;
        public override void Start(ICoreAPI api) { api.RegisterBlockClass("EuropeMountainSnow",typeof(MountainSnow)); }

        // Po vanilla generatorze terenu, żeby nadpisać jego wynik.
        public override double ExecuteOrder() => 0.5;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Logger.Notification("[EuropeGen] wersja {0} uruchomiona", Version);
            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.InitWorldGenerator(InitWorldGen, "standard");
            api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");
            api.Event.ChunkColumnGeneration(FinalizeRiverWater, EnumWorldGenPass.PreDone, "standard");
            api.Event.ChunkColumnGeneration(FinalizeGeography, EnumWorldGenPass.PreDone, "standard");
            api.Event.MapRegionGeneration(GenerateRegionalMaps,"standard");

            api.ChatCommands.Create("europe")
                .WithDescription("EuropeGen: narzędzia")
                .RequiresPrivilege(Privilege.tp)
                .RequiresPlayer()
                .BeginSubCommand("tp")
                    .WithDescription("Teleport do miejsca z places.json, np. /europe tp paryz")
                    .WithArgs(api.ChatCommands.Parsers.Word("miejsce"))
                    .HandleWith(OnTp)
                .EndSubCommand()
                .BeginSubCommand("story")
                    .WithDescription("Rzeczywiste pozycje i stan lokacji fabularnych")
                    .HandleWith(OnStory)
                .EndSubCommand()
                .BeginSubCommand("info")
                    .WithDescription("Gdzie jestem na mapie (diagnostyka)")
                    .HandleWith(OnInfo)
                .EndSubCommand()
                .BeginSubCommand("places")
                    .WithDescription("Lista miejsc")
                    .HandleWith(_ => TextCommandResult.Success(string.Join(", ", places.Keys)))
                .EndSubCommand();
        }

        // ------------------------------------------------------------ wczytywanie
        void OnSaveGameLoaded()
        {
            storyWasSaved=sapi.WorldManager.SaveGame.GetData("storystructurelocations")!=null;
            // Dane mogą leżeć w VintagestoryData\europegen (domyślna ścieżka moda) albo w VintagestoryData\ModData\europegen.
            string dir = sapi.GetOrCreateDataPath("europegen");
            string path = Path.Combine(dir, "europe.bin");
            Stream bundledMap = typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen.europe.bin");
            if (bundledMap == null && !File.Exists(path))
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir;
                string altDir = Path.Combine(parent, "ModData", "europegen");
                string altPath = Path.Combine(altDir, "europe.bin");
                if (File.Exists(altPath))
                {
                    dir = altDir;
                    path = altPath;
                }
                else
                {
                    sapi.Logger.Error("[EuropeGen] Brak pliku europe.bin. Szukano w: {0} oraz {1}", path, altPath);
                    return;
                }
            }
            sapi.Logger.Notification("[EuropeGen] Wczytuję dane z {0}", path);

            using (var br = new BinaryReader(bundledMap ?? File.OpenRead(path)))
            {
                mapW = br.ReadInt32();
                mapH = br.ReadInt32();
                bpp = br.ReadInt32();
                spawnPx = br.ReadInt32();
                spawnPz = br.ReadInt32();
                hdrSea = br.ReadInt32();

                int n = mapW * mapH;
                byte[] raw = br.ReadBytes(n * 2);
                hmap = new ushort[n];
                Buffer.BlockCopy(raw, 0, hmap, 0, raw.Length);

                raw = br.ReadBytes(n * 2);
                wmap = new ushort[n];
                Buffer.BlockCopy(raw, 0, wmap, 0, raw.Length);

                rmap = br.ReadBytes(n);
            }

            using (var flowStream = typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen.riverflow.bin"))
            {
                if (flowStream != null)
                {
                    riverFlow = new byte[mapW * mapH];
                    flowStream.ReadExactly(riverFlow);
                }
            }

            massif = ReadByteResource("massif.bin");
            bankMask = ReadByteResource("bankmask.bin");
            flowingMask = ReadByteResource("flowingmask.bin");
            uplift = ReadByteResource("uplift.bin");
            using (var surfaceStream = typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen.riversurface.bin"))
            {
                if (surfaceStream != null)
                {
                    byte[] raw = new byte[mapW*mapH*4]; surfaceStream.ReadExactly(raw);
                    riverSurface = new float[mapW*mapH]; Buffer.BlockCopy(raw,0,riverSurface,0,raw.Length);
                }
            }

            string pp = Path.Combine(dir, "places.json");
            using var bundledPlaces = typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen.places.json");
            if (bundledPlaces != null)
            {
                using var reader = new StreamReader(bundledPlaces);
                places = JsonConvert.DeserializeObject<Dictionary<string, double[]>>(reader.ReadToEnd()) ?? places;
            }
            else if (File.Exists(pp))
                places = JsonConvert.DeserializeObject<Dictionary<string, double[]>>(File.ReadAllText(pp)) ?? places;

            biomes=ReadJsonResource<List<BiomeZone>>("biomes.json") ?? biomes;
            using(var oasisStream=typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen.oasisdistance.bin"))
            if(oasisStream!=null) {
                oasisDistance=new ushort[mapW*mapH];
                using var reader=new BinaryReader(oasisStream);
                for(int i=0;i<oasisDistance.Length;i++)oasisDistance[i]=reader.ReadUInt16();
            }
            resourceZones=ReadJsonResource<List<ResourceZone>>("resource-zones.json") ?? resourceZones;
            foreach(var zone in resourceZones)foreach(var code in zone.Deposits)controlledDeposits.Add(code);

            sapi.Logger.Notification("[EuropeGen] Mapa {0}x{1} px, {2} bl/px, świat {3}x{4} bloków",
                mapW, mapH, bpp, mapW * bpp, mapH * bpp);
        }

        bool offsetsReady;

        T ReadJsonResource<T>(string name)
        {
            using var stream=typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen."+name);
            if(stream==null)return default;
            using var reader=new StreamReader(stream);
            return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
        }

        byte[] ReadByteResource(string name)
        {
            using var stream = typeof(EuropeGenMod).Assembly.GetManifestResourceStream("EuropeGen."+name);
            if (stream == null) return null;
            byte[] values = new byte[mapW*mapH]; stream.ReadExactly(values); return values;
        }

        // Rozmiar świata jest znany dopiero po "Server map set" (po SaveGameLoaded), więc liczymy dopiero przy starcie generatorów.
        // W VS współrzędne zaczynają się od 0, a środek leży w MapSize/2 (domyślnie 512000).
        // Gra spawnuje graczy przy środku, więc stawiamy tam punkt startowy mapy (Warszawa).
        void EnsureOffsets()
        {
            if (offsetsReady || hmap == null) return;

            int sx = sapi.WorldManager.MapSizeX, sz = sapi.WorldManager.MapSizeZ;
            if (sx <= 0) sx = 1024000;
            if (sz <= 0) sz = 1024000;
            int cx = sx / 2, cz = sz / 2;

            if (AutoFitWorld)
            {
                double spanX = 2.0 * Math.Max(spawnPx, mapW - 1 - spawnPx);
                double spanZ = 2.0 * Math.Max(spawnPz, mapH - 1 - spawnPz);
                bpp = WorldCoverage * Math.Min(sx / spanX, sz / spanZ);
                sapi.Logger.Notification("[EuropeGen] Skala automatyczna {0:F3} bloków/piksel; wycinek {1:F0}x{2:F0} bloków", bpp, mapW*bpp, mapH*bpp);
            }

            offsetX = cx - (int)Math.Round(spawnPx * bpp);
            offsetZ = cz - (int)Math.Round(spawnPz * bpp);
            offsetsReady = true;

            sapi.Logger.Notification("[EuropeGen] Rozmiar świata {0}x{1}, środek ({2},{3}), lewy górny róg mapy: ({4},{5})",
                sx, sz, cx, cz, offsetX, offsetZ);
        }

        int PxToWorld(double px, int offset) => offset + (int)(px * bpp);

        void InitWorldGen()
        {
            EnsureOffsets();
            seaLevel = sapi.World.SeaLevel;
            worldHeightLimit = sapi.WorldManager.MapSizeY;
            shift = seaLevel - (hdrSea == 0 ? seaLevel : hdrSea); // gdyby świat miał inną wysokość
            SetDrySpawnNearEngland();

            int fallbackRock = IdOr("game:rock-granite", 0);
            idBedrock = IdOr("game:mantle", fallbackRock);   // najniższa warstwa świata
            idSoil = IdOr("game:soil-medium-normal", fallbackRock);
            idSoilBare = IdOr("game:soil-medium-none", fallbackRock);
            idWater = IdOr("game:water-still-7", 0);
            idSnow = IdOr("game:snowlayer-3", 0);
            idSnowBlock = IdOr("europegen:mountainsnow", IdOr("game:snowblock",0));
            idGlacierIce = IdOr("game:packedglacierice",0);
            idWaterfall = IdOr("game:water-d-7", idWater);
            string[] directions = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };
            flowingWaterIds = new int[directions.Length];
            for (int i = 0; i < directions.Length; i++)
                flowingWaterIds[i] = IdOr("game:water-" + directions[i] + "-" + SurfaceFlowLevel, idWater);
            waterLevelIds = new int[9,8];
            for(int direction=0;direction<9;direction++)
            for(int level=1;level<=7;level++)
                waterLevelIds[direction,level] = IdOr("game:water-"+(direction==8?"still":directions[direction])+"-"+level,idWater);
            int fallbackSand = IdOr("game:sand-granite", idSoil);

            rockIds = new int[RockCodes.Length];
            sandIds = new int[RockCodes.Length];
            for (int i = 0; i < RockCodes.Length; i++)
            {
                rockIds[i] = IdOr("game:rock-" + RockCodes[i], fallbackRock);
                sandIds[i] = IdOr("game:sand-" + RockCodes[i], fallbackSand);
            }
            PlanStoryLocations();
        }

        int IdOr(string code, int fallback)
        {
            var b = sapi.World.GetBlock(new AssetLocation(code));
            if (b == null)
            {
                sapi.Logger.Warning("[EuropeGen] Nie ma bloku {0}", code);
                return fallback;
            }
            return b.Id;
        }

        // ------------------------------------------------------------ próbkowanie mapy
        bool Inside(double fx, double fz) => fx >= 0 && fz >= 0 && fx < mapW - 1 && fz < mapH - 1;

        int Nearest(int wx, int wz)
        {
            int px = (int)Math.Round((wx - offsetX) / (double)bpp);
            int pz = (int)Math.Round((wz - offsetZ) / (double)bpp);
            px = Math.Clamp(px, 0, mapW - 1);
            pz = Math.Clamp(pz, 0, mapH - 1);
            return pz * mapW + px;
        }

        // Wysokość powierzchni; bilinearnie, ale piksele rzek/jezior bierzemy dokładnie.
        int HeightAt(int wx, int wz)
        {
            if (hmap == null) return seaLevel - 20 - ExtraSeaDepth;

            double fx = (wx - offsetX) / (double)bpp;
            double fz = (wz - offsetZ) / (double)bpp;
            if (!Inside(fx, fz)) return seaLevel - 20 - ExtraSeaDepth;

            int near = Nearest(wx, wz);
            if (wmap[near] > 0)
            {
                if(riverSurface!=null && riverFlow!=null && riverFlow[near]>0)
                {
                    int depth=Math.Max(1,(int)wmap[near]-hmap[near]);
                    return WaterAt(wx,wz).top-depth;
                }
                return hmap[near] + shift;
            }
            if(bankMask!=null && bankMask[near]>0)return hmap[near]+shift;

            int x0 = (int)fx, z0 = (int)fz;
            double tx = fx - x0, tz = fz - z0;
            double h00 = hmap[z0 * mapW + x0];
            double h10 = hmap[z0 * mapW + x0 + 1];
            double h01 = hmap[(z0 + 1) * mapW + x0];
            double h11 = hmap[(z0 + 1) * mapW + x0 + 1];
            double h = h00 * (1 - tx) * (1 - tz) + h10 * tx * (1 - tz)
                     + h01 * (1 - tx) * tz + h11 * tx * tz;
            int surface = (int)Math.Round(h) + shift;
            if(uplift!=null && surface>seaLevel)
            {
                int relief=surface-seaLevel;
                double extra=relief<=20?Math.Min(3,relief*lowlandRiseFactor):Math.Max(0,3-(relief-20)/3.0);
                double strength=(uplift[z0*mapW+x0]*(1-tx)*(1-tz)
                    +uplift[z0*mapW+x0+1]*tx*(1-tz)
                    +uplift[(z0+1)*mapW+x0]*(1-tx)*tz
                    +uplift[(z0+1)*mapW+x0+1]*tx*tz)/255.0;
                surface+=(int)Math.Round(extra*strength,MidpointRounding.AwayFromZero);
            }
            if (massif != null)
            {
                double strength = (massif[z0*mapW+x0]*(1-tx)*(1-tz)
                    +massif[z0*mapW+x0+1]*tx*(1-tz)
                    +massif[(z0+1)*mapW+x0]*(1-tx)*tz
                    +massif[(z0+1)*mapW+x0+1]*tx*tz)/255.0;
                double room = Math.Max(0,worldHeightLimit-MountainRoofMargin-surface);
                double lift = strength*(worldHeightLimit-seaLevel)*MassifLiftFraction;
                if(room>0)surface+=(int)Math.Round(room*(1-Math.Exp(-lift/room)));
            }
            if (surface > seaLevel + 3 && IsDesert(wx,wz))
            {
                // Łagodne wydmy w pustynnej części danych; skala pozioma także skaluje wydmy.
                double dune = 1.5*(1+Math.Sin(fx/9.0+0.5*Math.Sin(fz/17.0)));
                surface += (int)Math.Round(dune);
            }
            return surface < seaLevel ? surface - ExtraSeaDepth : surface;
        }

        (int top, int fluidId) WaterAt(int wx,int wz)
        {
            int near=Nearest(wx,wz),direction=riverFlow==null?0:riverFlow[near];
            int top=wmap[near]>0?wmap[near]+shift:seaLevel;
            // Pełna stojąca woda w zwykłych rzekach. Wysokie górskie odcinki mają osobną maskę.
            if(direction>0 && (flowingMask==null || flowingMask[near]==0))return(top,idWater);
            int level=SurfaceFlowLevel;
            if(riverSurface!=null && direction>0)
            {
                double fx=(wx-offsetX)/bpp,fz=(wz-offsetZ)/bpp;
                int x0=(int)fx,z0=(int)fz;
                double tx=fx-x0,tz=fz-z0,total=0,weights=0;
                for(int dz=0;dz<=1;dz++)
                for(int dx=0;dx<=1;dx++)
                {
                    int x=x0+dx,z=z0+dz;
                    if(x<0||z<0||x>=mapW||z>=mapH)continue;
                    int index=z*mapW+x;
                    if(riverFlow[index]==0)continue;
                    double weight=(dx==0?1-tx:tx)*(dz==0?1-tz:tz);
                    total+=riverSurface[index]*weight;weights+=weight;
                }
                double target=(weights>1e-9?total/weights:riverSurface[near])+shift+1;
                top=(int)Math.Ceiling(target-1e-5)-1;
                level=Math.Clamp((int)Math.Round((target-top)*7),1,7);
            }
            int fluidId = waterLevelIds!=null && direction>0
                ?waterLevelIds[direction<=8?direction-1:8,level]
                :direction>0&&direction<=8&&flowingWaterIds!=null?flowingWaterIds[direction-1]:idWater;
            return(top,fluidId);
        }

        void SetDrySpawnNearEngland()
        {
            if (hmap == null) return;
            if(!places.TryGetValue("londyn",out var england))return;
            int startX=(int)Math.Round(england[0]),startZ=(int)Math.Round(england[1]);
            for (int distance = 0; distance <= 12; distance++)
            for (int dz = -distance; dz <= distance; dz++)
            for (int dx = -distance; dx <= distance; dx++)
            {
                if (Math.Abs(dx) + Math.Abs(dz) != distance) continue;
                int px = startX + dx, pz = startZ + dz;
                if (px < 0 || pz < 0 || px >= mapW || pz >= mapH) continue;
                int cell = pz * mapW + px;
                if (wmap[cell] > 0 || hmap[cell] <= hdrSea) continue;
                int x = PxToWorld(px, offsetX), z = PxToWorld(pz, offsetZ);
                int y = Math.Clamp(HeightAt(x, z) + 2, 5, sapi.WorldManager.MapSizeY - 2);
                sapi.WorldManager.SaveGame.DefaultSpawn = new PlayerSpawnPos(x, y, z);
                return;
            }
        }

        bool ExposeRockAt(int wx, int wz, int height, bool wet)
        {
            if (wet || height < seaLevel + RockStartAboveSea) return false;
            var geo=GeographyAt(wx,wz);
            if (height >= SnowLine(geo.lon,geo.lat)) return true;
            int steepness = Math.Max(
                Math.Max(Math.Abs(height - HeightAt(wx - 1, wz)), Math.Abs(height - HeightAt(wx + 1, wz))),
                Math.Max(Math.Abs(height - HeightAt(wx, wz - 1)), Math.Abs(height - HeightAt(wx, wz + 1))));
            bool mountain=massif!=null && massif[Nearest(wx,wz)]>=38 && height>=seaLevel+25;
            return steepness >= (mountain?2:RockSlopeThreshold);
        }

        (double lon, double lat) GeographyAt(int wx, int wz)
        {
            // Ta sama sferyczna LAEA co w przygotowaniu oryginalnej mapy.
            double x = 2000000 + (wx-offsetX)/bpp*1000 - 4321000;
            double y = 5500000 - (wz-offsetZ)/bpp*1000 - 3210000;
            double rho = Math.Max(Math.Sqrt(x*x+y*y), 1e-9);
            double c = 2*Math.Asin(Math.Min(rho/(2*6371008.8),1));
            double p0 = 52*Math.PI/180, s = Math.Sin(c), co = Math.Cos(c);
            double lat = Math.Asin(Math.Clamp(co*Math.Sin(p0)+y*s*Math.Cos(p0)/rho,-1,1));
            double lon = 10*Math.PI/180+Math.Atan2(x*s,rho*Math.Cos(p0)*co-y*Math.Sin(p0)*s);
            return (lon*180/Math.PI, lat*180/Math.PI);
        }

        bool IsDesert(int wx, int wz)
        {
            var geo = GeographyAt(wx,wz);
            return geo.lat >= 20 && geo.lat <= 32 && geo.lon >= -17 && geo.lon <= 35;
        }

        // Przybliżona granica północnej Afryki: nie obejmuje Iberii ani wysp Europy.
        static readonly double[] AfricanX={-17,-17,-11,-5.6,-5,-2,8,11,12,19,23,25,34,35,35};
        static readonly double[] AfricanY={20,28,36,35.9,35.9,35.2,37.5,37.6,33,33,32.3,31.8,31.4,22,20};
        static bool IsAfricanLand(double lon,double lat)
        {
            var xs=AfricanX;var ys=AfricanY;
            bool inside=false;
            for(int i=0,j=xs.Length-1;i<xs.Length;j=i++)
                if((ys[i]>lat)!=(ys[j]>lat) && lon<(xs[j]-xs[i])*(lat-ys[i])/(ys[j]-ys[i])+xs[i])inside=!inside;
            return inside;
        }

        int SnowLine(double lon,double lat)
        {
            float temperature=IsAfricanLand(lon,lat)?29:BiomeAt(lon,lat)?.Temperature??11;
            return seaLevel+SnowAboveSea+(int)Math.Round((temperature-11)*0.8);
        }

        double OasisAt(int wx,int wz)
        {
            if(oasisDistance==null || !Inside((wx-offsetX)/bpp,(wz-offsetZ)/bpp))return 0;
            int near=Nearest(wx,wz);
            if(wmap[near]>0 || HeightAt(wx,wz)<=seaLevel)return 0;
            // Odległość w pikselach / 8; promień oazy pozostaje w blokach przy każdej skali świata.
            double distance=oasisDistance[near]/8.0*bpp;
            var geo=GeographyAt(wx,wz);
            return Math.Clamp((OasisRadiusAt(geo.lon,geo.lat)-distance)/24.0,0,1);
        }

        static double OasisRadiusAt(double lon,double lat)
            => lon>=24 && lon<=36 && lat>=22 && lat<=32.5 ? OasisRadius-2 : OasisRadius;

        BiomeZone BiomeAt(double lon,double lat)
        {
            BiomeZone found=null;
            foreach(var zone in biomes)
                if(lon>=zone.West&&lon<=zone.East&&lat>=zone.South&&lat<=zone.North)found=zone;
            return found;
        }

        double DepositFactor(string code,double lon,double lat)
        {
            if(!controlledDeposits.Contains(code))return 1;
            double factor=0;
            foreach(var zone in resourceZones)
            {
                if(lon<zone.West||lon>zone.East||lat<zone.South||lat>zone.North)continue;
                if(Array.Exists(zone.Deposits,item=>string.Equals(item,code,StringComparison.OrdinalIgnoreCase)))factor=Math.Max(factor,zone.Factor);
            }
            return factor;
        }

        void GenerateRegionalMaps(IMapRegion region,int regionX,int regionZ,ITreeAttribute parameters)
        {
            EnsureOffsets();
            if(hmap==null)return;
            int size=sapi.WorldManager.RegionSize;
            if(size<=0)return;
            ApplyRegionalMap(region.ClimateMap,regionX,regionZ,size,0);
            ApplyRegionalMap(region.ForestMap,regionX,regionZ,size,1);
            ApplyRegionalMap(region.ShrubMap,regionX,regionZ,size,2);
            if(region.OreMaps!=null)
            foreach(var pair in region.OreMaps)
            {
                var map=pair.Value;
                if(map==null||map.Data==null||map.InnerSize<=0)continue;
                for(int z=0;z<map.Size;z++)for(int x=0;x<map.Size;x++)
                {
                    int wx=regionX*size+(int)Math.Round((x-map.TopLeftPadding)*(double)size/map.InnerSize);
                    int wz=regionZ*size+(int)Math.Round((z-map.TopLeftPadding)*(double)size/map.InnerSize);
                    if(!Inside((wx-offsetX)/bpp,(wz-offsetZ)/bpp))continue;
                    var geo=GeographyAt(wx,wz);double factor=DepositFactor(pair.Key,geo.lon,geo.lat);
                    int index=z*map.Size+x,previous=map.Data[index];
                    int density=Math.Clamp((int)Math.Round((previous&255)*factor),0,255);
                    map.Data[index]=(previous&~255)|density;
                }
            }
            region.DirtyForSaving=true;
        }

        void ApplyRegionalMap(IntDataMap2D map,int regionX,int regionZ,int size,int kind)
        {
            if(map==null||map.Data==null||map.InnerSize<=0)return;
            double step=(double)size/map.InnerSize;
            for(int z=0;z<map.Size;z++)for(int x=0;x<map.Size;x++)
            {
                int wx=regionX*size+(int)Math.Round((x-map.TopLeftPadding)*step);
                int wz=regionZ*size+(int)Math.Round((z-map.TopLeftPadding)*step);
                if(!Inside((wx-offsetX)/bpp,(wz-offsetZ)/bpp))continue;
                var geo=GeographyAt(wx,wz);var biome=BiomeAt(geo.lon,geo.lat);
                if(biome==null)continue;
                // Uwzględniamy także szczyty między próbkami mapy klimatu.
                int maxHeight=HeightAt(wx,wz);
                for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++)
                    maxHeight=Math.Max(maxHeight,HeightAt(wx+(int)(dx*step/2),wz+(int)(dz*step/2)));
                bool africa=IsAfricanLand(geo.lon,geo.lat);
                double oasis=africa?OasisAt(wx,wz):0;
                int snowLine=SnowLine(geo.lon,geo.lat);
                bool snowy=maxHeight>=snowLine;
                int index=z*map.Size+x,old=map.Data[index];
                if(kind==0)
                {
                    float baseTemperature=africa?29:biome.Temperature;
                    double mountainCooling=Math.Clamp((maxHeight-(snowLine-35))/35.0,0,1);
                    float temperature=(float)(baseTemperature+(Math.Min(baseTemperature,SnowClimateTemperature)-baseTemperature)*mountainCooling);
                    int rain=snowy?Math.Max(150,biome.Rain):africa?(int)Math.Round(18+oasis*182):biome.Rain;
                    map.Data[index]=(Climate.DescaleTemperature(temperature)<<16)|(Math.Clamp(rain,0,255)<<8)|(old&255);
                }
                else
                {
                    int target=snowy?0:africa?(kind==1?0:3):kind==1?biome.Forest:biome.Shrubs;
                    if(africa&&!snowy)target=(int)Math.Round(kind==1?oasis*160:3+oasis*117);
                    int near=Nearest(wx,wz),ground=HeightAt(wx,wz);
                    double vegetationFade=Math.Clamp((snowLine-ground)/(kind==1?30.0:15.0),0,1);
                    target=(int)Math.Round(target*vegetationFade);
                    if(wmap[near]>0 || ground<seaLevel)target=0;
                    else if(ground<=seaLevel+1)target=Math.Min(target,kind==1?5:10);
                    double variation=0.60+(old&255)/255.0*0.55;
                    map.Data[index]=(old&~255)|Math.Clamp((int)Math.Round(target*variation),0,255);
                }
            }
        }

        static uint DepositHash(int x, int z, int seed)
        {
            unchecked
            {
                uint value = (uint)(x*73856093 ^ z*19349663 ^ seed);
                value ^= value>>16; value *= 0x7feb352d; value ^= value>>15;
                return value;
            }
        }

        bool[] ProtectedColumns(IChunkColumnGenerateRequest req)
        {
            bool[] result=new bool[CS*CS];
            var regionsToRead=new HashSet<IMapRegion>();
            var current=req.Chunks[0].MapChunk.MapRegion;
            if(current!=null)regionsToRead.Add(current);
            int size=sapi.WorldManager.RegionSize;
            if(size>0)
            {
                int rx=req.ChunkX*CS/size,rz=req.ChunkZ*CS/size;
                for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++)
                {
                    if(rx+dx<0 || rz+dz<0)continue;
                    var region=sapi.WorldManager.GetMapRegion(rx+dx,rz+dz);
                    if(region!=null)regionsToRead.Add(region);
                }
            }
            int originX=req.ChunkX*CS,originZ=req.ChunkZ*CS;
            foreach(var region in regionsToRead)
            {
                var structures=region.GeneratedStructures;
                if(structures==null)continue;
                lock(structures)
                foreach(var structure in structures)
                {
                    var box=structure.Location;
                    if(box==null)continue;
                    int x1=Math.Max(0,box.X1-4-originX),x2=Math.Min(CS-1,box.X2+4-originX);
                    int z1=Math.Max(0,box.Z1-4-originZ),z2=Math.Min(CS-1,box.Z2+4-originZ);
                    for(int z=z1;z<=z2;z++)for(int x=x1;x<=x2;x++)result[z*CS+x]=true;
                }
            }
            return result;
        }

        void FinalizeGeography(IChunkColumnGenerateRequest req)
        {
            int worldHeight = req.Chunks.Length*CS;
            bool seededSnow=false;
            var protectedColumns=ProtectedColumns(req);
            for (int lz=0;lz<CS;lz++)
            for (int lx=0;lx<CS;lx++)
            {
                if(protectedColumns[lz*CS+lx])continue;
                int wx=req.ChunkX*CS+lx,wz=req.ChunkZ*CS+lz;
                if (!Inside((wx-offsetX)/bpp,(wz-offsetZ)/bpp)) continue;
                int near=Nearest(wx,wz);
                int floor=Math.Clamp(HeightAt(wx,wz),4,worldHeight-2);
                bool wet=wmap[near]>0 || floor<=seaLevel;
                var geo=GeographyAt(wx,wz);
                bool desert=!wet && IsAfricanLand(geo.lon,geo.lat);
                bool oasis=desert && OasisAt(wx,wz)>0.15;
                bool snowy=floor>=SnowLine(geo.lon,geo.lat);
                int rock=rockIds[Math.Min((int)rmap[near],RockCodes.Length-1)];
                // Złoża tworzy generator gry według regionalnych OreMaps; nie nadpisujemy istniejących rud.
                if (wet)continue;
                bool bareMassif=massif!=null && massif[near]>=38 && ExposeRockAt(wx,wz,floor,false);
                if(bareMassif)
                {
                    for(int y=Math.Max(1,floor-SoilLayerThickness+1);y<=floor;y++)
                    {
                        var data=req.Chunks[y/CS].Data;int index=((y%CS)*CS+lz)*CS+lx;
                        data.SetBlockUnsafe(index,rock);data.SetFluid(index,0);
                    }
                }
                if(desert)
                {
                    // Jedna odmiana piasku także na odsłoniętych bokach i pod natywnymi warstwami.
                    for(int y=1;y<=floor;y++) {
                        var data=req.Chunks[y/CS].Data;int index=((y%CS)*CS+lz)*CS+lx;
                        int block=data.GetBlockIdUnsafe(index);
                        if(block!=0 && sapi.World.Blocks[block].BlockMaterial==EnumBlockMaterial.Sand)
                            data.SetBlockUnsafe(index,sandIds[2]);
                    }
                }
                if (desert && !oasis)
                {
                    for(int y=Math.Max(1,floor-3);y<=floor;y++)
                    {
                        var data=req.Chunks[y/CS].Data;int index=((y%CS)*CS+lz)*CS+lx;
                        data.SetBlockUnsafe(index,sandIds[2]);data.SetFluid(index,0);
                    }
                }
                if(oasis&&!snowy&&!bareMassif)
                {
                    for(int y=Math.Max(1,floor-SoilLayerThickness+1);y<=floor;y++) {
                        var data=req.Chunks[y/CS].Data;int index=((y%CS)*CS+lz)*CS+lx;
                        data.SetBlockUnsafe(index,y==floor?idSoil:idSoilBare);
                    }
                }
                if ((desert&&!oasis) || bareMassif || snowy)
                {
                    for(int y=floor+1;y<worldHeight;y++)
                    {
                        var data=req.Chunks[y/CS].Data;int index=((y%CS)*CS+lz)*CS+lx;
                        int existing=data.GetBlockIdUnsafe(index);
                        if(existing!=0)
                        {
                            var mat=sapi.World.Blocks[existing].BlockMaterial;
                            bool naturalTrunk=mat==EnumBlockMaterial.Wood && sapi.World.Blocks[existing].Code?.Path.StartsWith("log-grown-")==true;
                            if(mat==EnumBlockMaterial.Plant || naturalTrunk || mat==EnumBlockMaterial.Leaves)
                                data.SetBlockUnsafe(index,0);
                        }
                    }
                    if(snowy && idSnow!=0)
                    {
                        int y=floor+1,index=((y%CS)*CS+lz)*CS+lx;
                        int existingSnow=req.Chunks[y/CS].Data.GetBlockIdUnsafe(index);
                        if(existingSnow!=0 && sapi.World.Blocks[existingSnow].BlockMaterial!=EnumBlockMaterial.Snow)continue;
                        if(idGlacierIce!=0 && floor>=SnowLine(geo.lon,geo.lat)+20)
                        {
                            for(int iceY=Math.Max(1,floor-1);iceY<=floor;iceY++)
                            {
                                int iceIndex=((iceY%CS)*CS+lz)*CS+lx;
                                req.Chunks[iceY/CS].Data.SetBlockUnsafe(iceIndex,idGlacierIce);
                            }
                        }
                        req.Chunks[y/CS].Data.SetBlockUnsafe(index,idSnowBlock!=0?idSnowBlock:idSnow);
                        int column=lz*CS+lx;
                        req.Chunks[0].MapChunk.SnowAccum[column]=idSnowBlock!=0?8f:3f;
                        seededSnow=true;
                        req.Chunks[0].MapChunk.RainHeightMap[column]=(ushort)y;
                        if(y>req.Chunks[0].MapChunk.YMax)req.Chunks[0].MapChunk.YMax=(ushort)y;
                    }
                }
            }
            if(seededSnow)req.Chunks[0].MapChunk.SetModdata("lastSnowAccumUpdateTotalHours",SerializerUtil.Serialize(sapi.World.Calendar.TotalHours));
        }

        // ------------------------------------------------------------ generator
        void OnChunkColumnGen(IChunkColumnGenerateRequest req)
        {
            if (hmap == null) return; // brak danych: zostaje vanilla
            EnsureOffsets();

            var chunks = req.Chunks;
            var mapChunk = chunks[0].MapChunk;
            int worldHeight = chunks.Length * CS;

            for (int lz = 0; lz < CS; lz++)
            {
                for (int lx = 0; lx < CS; lx++)
                {
                    int wx = req.ChunkX * CS + lx;
                    int wz = req.ChunkZ * CS + lz;

                    int h = Math.Clamp(HeightAt(wx, wz), 4, worldHeight - 2);
                    int vanillaFloor=mapChunk.WorldGenTerrainHeightMap[lz*CS+lx];

                    if (!loggedFirstColumn)
                    {
                        loggedFirstColumn = true;
                        sapi.Logger.Notification("[EuropeGen] pierwsza kolumna: świat ({0},{1}) piksel ({2:F0},{3:F0}) w mapie: {4}, wysokość {5}",
                            wx, wz, (wx - offsetX) / (double)bpp, (wz - offsetZ) / (double)bpp,
                            Inside((wx - offsetX) / (double)bpp, (wz - offsetZ) / (double)bpp), h);
                    }

                    int near = Nearest(wx, wz);
                    bool inMap = Inside((wx - offsetX) / (double)bpp, (wz - offsetZ) / (double)bpp);
                    int rock = inMap ? Math.Min((int)rmap[near], RockCodes.Length - 1) : 0;

                    var water = inMap ? WaterAt(wx,wz) : (top:seaLevel,fluidId:idWater);
                    int waterTop = water.top;
                    int direction = inMap && riverFlow != null ? riverFlow[near] : 0;
                    // Nie podnosimy pojedynczych kolumn do poziomu sąsiada:
                    // powodowało to sztuczne ścianki na powierzchni rzek.
                    int fluidTop = waterTop;
                    bool wet = waterTop > h;
                    var geo=GeographyAt(wx,wz);
                    bool africa=inMap && IsAfricanLand(geo.lon,geo.lat);
                    bool oasis=africa && OasisAt(wx,wz)>0.15;
                    bool sandy = wet || h <= seaLevel + 1 || (africa&&!oasis);
                    bool exposedRock = ExposeRockAt(wx, wz, h, wet);

                    for (int y = 0; y < worldHeight; y++)
                    {
                        int block;
                        bool fluid = false;

                        if (y == 0) block = idBedrock;
                        else if (y <= h - SoilLayerThickness) block = rockIds[rock];
                        else if (y <= h) block = exposedRock ? rockIds[rock]
                            : sandy ? sandIds[africa?2:rock] : (y == h ? idSoil : idSoilBare);
                        else
                        {
                            block = idAir;
                            fluid = wet && y <= fluidTop;
                        }

                        var data = chunks[y / CS].Data;
                        int idx = ((y % CS) * CS + lz) * CS + lx;
                        // Zachowujemy podziemne pustki generatora jaskiń, poniżej obu powierzchni.
                        bool vanillaCave=y>1 && y<h-SoilLayerThickness && y<vanillaFloor-8 && data.GetBlockIdUnsafe(idx)==0;
                        if(vanillaCave){data.SetBlockUnsafe(idx,0);continue;}
                        data.SetBlockUnsafe(idx, block);
                        int waterId = y == waterTop ? water.fluidId : idWater;
                        data.SetFluid(idx, fluid ? waterId : 0);
                    }

                    int top = wet ? Math.Min(fluidTop, worldHeight - 1) : h;
                    int col = lz * CS + lx;
                    // Roślinność musi widzieć dno pod wodą, nie powierzchnię
                    // jako suchy teren. Mapa deszczu obejmuje powierzchnię wody.
                    mapChunk.WorldGenTerrainHeightMap[col] = (ushort)h;
                    if(mapChunk.TopRockIdMap!=null)mapChunk.TopRockIdMap[col]=rockIds[rock];
                    mapChunk.RainHeightMap[col] = (ushort)top;
                    if (top > mapChunk.YMax) mapChunk.YMax = (ushort)top;
                }
            }
        }

        // Po roślinności i dodatkowych warstwach terenu przywracamy drożność
        // tylko w zapisanych korytach. Nie dotykamy gór ani ziemi poza rzekami.
        void FinalizeRiverWater(IChunkColumnGenerateRequest req)
        {
            if (hmap == null || riverFlow == null) return;
            var protectedColumns=ProtectedColumns(req);
            int worldHeight = req.Chunks.Length * CS;
            for (int lz = 0; lz < CS; lz++)
            for (int lx = 0; lx < CS; lx++)
            {
                if(protectedColumns[lz*CS+lx])continue;
                int wx = req.ChunkX * CS + lx, wz = req.ChunkZ * CS + lz;
                if (!Inside((wx - offsetX) / (double)bpp, (wz - offsetZ) / (double)bpp)) continue;
                int near = Nearest(wx, wz), direction = riverFlow[near];
                if (direction == 0 || wmap[near] == 0) continue;
                int floor = Math.Clamp(HeightAt(wx, wz), 4, worldHeight - 2);
                var water = WaterAt(wx,wz);
                int top = Math.Clamp(water.top, floor + 1, worldHeight - 1);
                for (int y = floor + 1; y <= Math.Min(top + 2, worldHeight - 1); y++)
                {
                    var blocks = req.Chunks[y / CS].Data;
                    int index = ((y % CS) * CS + lz) * CS + lx;
                    if (y <= top)
                    {
                        blocks.SetBlockUnsafe(index, 0);
                        int fluid = y == top ? water.fluidId : idWater;
                        blocks.SetFluid(index, fluid);
                    }
                    else
                    {
                        int id = blocks.GetBlockIdUnsafe(index);
                        if (id != 0)
                        {
                            var material = sapi.World.Blocks[id].BlockMaterial;
                            if (material == EnumBlockMaterial.Soil || material == EnumBlockMaterial.Plant)
                                blocks.SetBlockUnsafe(index, 0);
                        }
                        blocks.SetFluid(index, 0);
                    }
                }
                int column = lz * CS + lx;
                req.Chunks[0].MapChunk.WorldGenTerrainHeightMap[column] = (ushort)floor;
                req.Chunks[0].MapChunk.RainHeightMap[column] = (ushort)top;
            }
        }

        // ------------------------------------------------------------ komenda
        void PlanStoryLocations()
        {
            if(storyWasSaved || hmap==null || !sapi.World.Config.GetBool("loreContent",true))return;
            var generator=sapi.ModLoader.GetModSystem<GenStoryStructures>();
            if(generator?.Structures==null)return;
            var targets=new Dictionary<string,string> {
                {"resonancearchive","rzym"},{"lazaret","berlin"},{"village","paryz"},
                {"devastationarea","dewastacja-afryka"},{"tobiascave","wieden"},{"treasurehunter","londyn"}
            };
            foreach(var pair in targets)
            {
                var location=generator.Structures.Get(pair.Key);
                if(location==null || !places.TryGetValue(pair.Value,out var pixel))continue;
                int targetX=PxToWorld(pixel[0],offsetX),targetZ=PxToWorld(pixel[1],offsetZ);
                if(pair.Key=="treasurehunter")targetX-=120;
                int footprint=Math.Max(location.Location.X2-location.Location.X1,location.Location.Z2-location.Location.Z1)/2+16;
                bool found=false; int newX=targetX,newZ=targetZ;
                // Szukamy lądu dla całej konstrukcji, nie tylko dla jej środka.
                for(int radius=0;radius<=40&&!found;radius++)
                for(int dz=-radius;dz<=radius&&!found;dz++)for(int dx=-radius;dx<=radius&&!found;dx++)
                {
                    if(radius>0 && Math.Abs(dx)!=radius && Math.Abs(dz)!=radius)continue;
                    int cx=targetX+(int)(dx*16*bpp),cz=targetZ+(int)(dz*16*bpp);
                    bool safe=true;
                    for(int zz=-1;zz<=1&&safe;zz++)for(int xx=-1;xx<=1;xx++)
                    {
                        int x=cx+xx*footprint,z=cz+zz*footprint;
                        if(!Inside((x-offsetX)/bpp,(z-offsetZ)/bpp)||HeightAt(x,z)<=seaLevel+3||wmap[Nearest(x,z)]>0){safe=false;break;}
                    }
                    if(safe){found=true;newX=cx;newZ=cz;}
                }
                if(!found){sapi.Logger.Warning("[EuropeGen] Nie znaleziono miejsca na lądzie dla {0}; zostaje pozycja gry",pair.Key);continue;}
                int shiftX=newX-location.CenterPos.X,shiftZ=newZ-location.CenterPos.Z;
                var box=location.Location;
                location.CenterPos.X=newX;location.CenterPos.Z=newZ;
                location.Location=new Cuboidi(box.X1+shiftX,box.Y1,box.Z1+shiftZ,box.X2+shiftX,box.Y2,box.Z2+shiftZ);
                generator.Structures.Set(pair.Key,location);
                generator.StoryStructureInstancesDirty=true;
                sapi.Logger.Notification("[EuropeGen] Lokacja {0}: okolice {1}, {2},{3}",pair.Key,pair.Value,newX,newZ);
            }
        }

        TextCommandResult OnStory(TextCommandCallingArgs args)
        {
            var generator=sapi.ModLoader.GetModSystem<GenStoryStructures>();
            if(generator?.Structures==null)return TextCommandResult.Error("Generator fabuły niedostępny.");
            var lines=new List<string>();
            foreach(var pair in generator.Structures)
            {
                var location=pair.Value;var pos=location.CenterPos;
                bool land=Inside((pos.X-offsetX)/bpp,(pos.Z-offsetZ)/bpp)&&HeightAt(pos.X,pos.Z)>seaLevel&&wmap[Nearest(pos.X,pos.Z)]==0;
                bool spawned=location.SchematicsSpawned!=null&&location.SchematicsSpawned.Count>0;
                lines.Add($"{pair.Key}: świat {pos.X},{pos.Y},{pos.Z}; ląd={land}; schematy wygenerowane={spawned}; /tpstoryloc {pair.Key}");
            }
            return TextCommandResult.Success(lines.Count==0?"Brak zaplanowanych lokacji. Sprawdź ustawienie fabuły świata.":string.Join("\n",lines));
        }

        TextCommandResult OnInfo(TextCommandCallingArgs args)
        {
            if (hmap == null) return TextCommandResult.Error("Mapa nie jest wczytana (brak europe.bin).");
            EnsureOffsets();

            var pos = args.Caller.Player.Entity.Pos;
            int wx = (int)pos.X, wz = (int)pos.Z;
            double fx = (wx - offsetX) / (double)bpp, fz = (wz - offsetZ) / (double)bpp;
            return TextCommandResult.Success(
                $"świat ({wx},{wz}), piksel ({fx:F0},{fz:F0}), w mapie: {Inside(fx, fz)}, wysokość z mapy: {HeightAt(wx, wz)}, " +
                $"offset ({offsetX},{offsetZ}), mapa {mapW}x{mapH}, wersja {Version}");
        }

        TextCommandResult OnTp(TextCommandCallingArgs args)
        {
            if (hmap == null) return TextCommandResult.Error("Mapa nie jest wczytana.");
            EnsureOffsets();

            string name = ((string)args[0]).ToLowerInvariant();
            if (!places.TryGetValue(name, out var p))
                return TextCommandResult.Error("Nieznane miejsce. Użyj /europe places");

            int wx = PxToWorld(p[0], offsetX);
            int wz = PxToWorld(p[1], offsetZ);
            int near = Nearest(wx, wz);
            int waterTop = WaterAt(wx,wz).top;
            int y = Math.Min(Math.Max(HeightAt(wx, wz), waterTop) + 3, sapi.WorldManager.MapSizeY - 2);

            args.Caller.Player.Entity.TeleportTo(new Vec3d(wx + 0.5, y, wz + 0.5));
            return TextCommandResult.Success($"Teleport: {name} ({wx}, {y}, {wz})");
        }
    }
}
