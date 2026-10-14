using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AladdinRug
{
    /// <summary>Synthetic images and cloth only: no Window, settings, Explorer, registry or desktop jobs.</summary>
    internal static class ImageChecks
    {
        private static string _root;
        private static int _passed, _failed;

        public static int Run(string output)
        {
            _root = Path.Combine(Path.GetFullPath(output), "image-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _passed = _failed = 0;
            Check("PNG source untouched/unlocked; alpha and colors survive reload/source deletion", ImportPng);
            Check("JPEG decode and eight EXIF orientations", JpegOrientations);
            Check("Embedded sRGB profile survives normalization", ColorProfile);
            Check("Cancel has no side effects", Cancel);
            Check("Corrupt, missing, empty and disguised non-image files keep the previous image", BadSources);
            Check("Encoded byte/dimension limits and proportional downsampling", Limits);
            Check("Failed replacement and clear preserve memory/disk; temporary files cleaned", SaveFailures);
            Check("Lighting persists independently, with atomic failure and clear to built-in", LightingPersistence);
            Check("Damaged managed image/settings fall back without rewriting them", BadSavedFiles);
            Check("Image face/back/flat have no backing, fringe, tint or shadow", PlainImage);
            Check("Wide, tall, square and extreme aspect ratios fit without cropping", ImageFit);
            Check("Flat image-to-cloth transition keeps decoded colors/alpha", FlatTransition);
            Check("Translucent layers composite by depth independent of insertion order", FragmentDepth);
            Check("Alpha 0/1/64/127/128/129/254/255 survive the image renderer", AlphaBoundaries);
            Check("Fold holes reveal and select the lower surface", FoldHole);
            Check("Front/back/fold/roll lighting changes RGB, never alpha", LightingAlpha);
            Check("Fully transparent image has no shadow or hits", TransparentImage);
            Check("Hit maps clear moved pixels and own the uploaded copy", HitMapLifetime);
            Check("Engine rejects holes and keeps a captured drag across them", EngineCapture);
            Check("A selected material node remains held after the frame gets stale", StaleSurfaceGrab);
            Console.WriteLine("Image checks: {0} passed, {1} failed. Fixtures: {2}", _passed, _failed, _root);
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string name, Action test)
        {
            try { test(); _passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void Require(bool value, string message = "Unexpected result") { if (!value) throw new InvalidOperationException(message); }
        private static string Dir() { string p = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        private static string Hash(byte[] p) => Convert.ToHexString(SHA256.HashData(p));
        private static byte[] Pixels(BitmapSource b) { var p = new byte[b.PixelWidth * b.PixelHeight * 4]; b.CopyPixels(p, b.PixelWidth * 4, 0); return p; }
        private static byte[] Alpha(byte[] p) => Enumerable.Range(0, p.Length / 4).Select(i => p[i * 4 + 3]).ToArray();
        private static void Save(BitmapSource b, string path, BitmapEncoder encoder = null, BitmapMetadata metadata = null, ReadOnlyCollection<ColorContext> colors = null)
        {
            encoder ??= new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(b, null, metadata, colors));
            using var f = File.Create(path); encoder.Save(f);
        }
        private static BitmapSource Pattern(int w = 64, int h = 64, bool alpha = true)
        {
            byte[] p = new byte[w * h * 4], levels = { 0, 1, 64, 127, 128, 129, 254, 255 };
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                byte a = alpha ? levels[Math.Min(7, x * 8 / w)] : (byte)255;
                if (alpha && x > w / 3 && x < 2 * w / 3 && y > h / 3 && y < 2 * h / 3) a = 0;
                int k = (y * w + x) * 4;
                p[k + (x < w / 2 ? 2 : 1)] = a;
                p[k + 3] = a;
            }
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, p, w * 4); b.Freeze(); return b;
        }
        private static void ImportPng()
        {
            string dir = Dir(), source = Path.Combine(dir, "source.png"), managed = Path.Combine(dir, "managed");
            Save(Pattern(), source);
            string hash = Hash(File.ReadAllBytes(source)); DateTime modified = File.GetLastWriteTimeUtc(source);
            var store = new TextureStore(managed);
            Require(store.TryImport(source, out string error), error);
            Require(Alpha(Pixels(Pattern())).SequenceEqual(Alpha(Pixels(store.Current))), "PNG alpha changed");
            Require(Pixels(Pattern()).Zip(Pixels(store.Current),(a,b)=>Math.Abs(a-b)).Max() <= 1, "Color conversion exceeded one level");
            Require(hash == Hash(File.ReadAllBytes(store.ImagePath)), "Managed copy differs from source bytes");
            using (var f = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None)) Require(f.Length > 0);
            Require(hash == Hash(File.ReadAllBytes(source)) && modified == File.GetLastWriteTimeUtc(source), "Source modified");
            File.Move(source, source + ".removed"); // preserve the fixture while making the original path unavailable
            Require(Pixels(store.Current).SequenceEqual(Pixels(new TextureStore(managed).Current)), "Restart depends on the source");
            Require(store.Current.IsFrozen);
        }
        private static void JpegOrientations()
        {
            // Four different corners distinguish every EXIF orientation away from JPEG edges.
            byte[] corners=Pixels(Pattern(80,40,false));
            for(int y=20;y<40;y++)for(int x=0;x<80;x++)
            { int k=(y*80+x)*4; corners[k]=255; corners[k+1]=corners[k+2]=(byte)(x<40?0:255); }
            var quadrants=BitmapSource.Create(80,40,96,96,PixelFormats.Pbgra32,null,corners,320);
            string plainDir = Dir(), plainPath = Path.Combine(plainDir, "plain.JPEG");
            Save(quadrants, plainPath, new JpegBitmapEncoder { QualityLevel = 100 });
            var plainStore = new TextureStore(Path.Combine(plainDir, "managed"));
            Require(plainStore.TryImport(plainPath, out string plainError), plainError);
            Require(plainStore.Current.PixelWidth == 80 && plainStore.Current.PixelHeight == 40, "JPEG without EXIF");
            int[][] expected = { new[] {2,1,0,3}, new[] {1,2,3,0}, new[] {3,0,1,2}, new[] {0,3,2,1},
                new[] {2,0,1,3}, new[] {0,2,3,1}, new[] {3,1,0,2}, new[] {1,3,2,0} };
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                string dir = Dir(), source = Path.Combine(dir, "source.jpg");
                var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
                Save(quadrants, source, new JpegBitmapEncoder { QualityLevel = 100 }, metadata);
                var store = new TextureStore(Path.Combine(dir, "managed"));
                Require(store.TryImport(source, out string error), error);
                int w = store.Current.PixelWidth, h = store.Current.PixelHeight;
                Require(w == (orientation >= 5 ? 40 : 80) && h == (orientation >= 5 ? 80 : 40), "EXIF aspect " + orientation);
                byte[] p = Pixels(store.Current);
                int n = 0;
                foreach (int y in new[] { h / 4, 3 * h / 4 }) foreach (int x in new[] { w / 4, 3 * w / 4 })
                {
                    int k = (y * w + x) * 4, channel = expected[orientation - 1][n++];
                    Require(p[k+3]==255, "JPEG alpha");
                    for(int c=0;c<3;c++) Require(channel==3 || c==channel ? p[k+c]>240 : p[k+c]<15, "EXIF colors " + orientation);
                }
                Require(Pixels(new TextureStore(Path.Combine(dir, "managed")).Current).SequenceEqual(p));
            }
        }
        private static void ColorProfile()
        {
            string dir = Dir(), source = Path.Combine(dir, "profile.png");
            Save(Pattern(), source, colors: new ReadOnlyCollection<ColorContext>(new[] { new ColorContext(PixelFormats.Bgra32) }));
            var store = new TextureStore(Path.Combine(dir, "managed"));
            Require(store.TryImport(source, out string error), error);
            byte[] expected = Pixels(Pattern()), actual = Pixels(store.Current);
            Require(expected.Zip(actual, (a,b) => Math.Abs(a-b)).Max() <= 1, "sRGB color/alpha changed");
        }
        private static void Cancel()
        {
            string directory = Path.Combine(Dir(), "absent"); var store = new TextureStore(directory);
            Require(!store.TryImport(null, out string error) && error == null && !Directory.Exists(directory));
            Require(store.TryClear(out string clearError) && !Directory.Exists(directory), "Clear absent: " + clearError);
        }
        private static void BadSources()
        {
            string dir = Dir(), good = Path.Combine(dir, "good.png"), bad = Path.Combine(dir, "bad.png"); Save(Pattern(), good);
            var store = new TextureStore(Path.Combine(dir, "managed")); Require(store.TryImport(good, out _));
            string hash = Hash(File.ReadAllBytes(store.ImagePath)); BitmapSource before = store.Current;
            foreach (byte[] data in new[] { Array.Empty<byte>(), new byte[] {1,2,3}, File.ReadAllBytes(good).Take(40).ToArray() })
            {
                File.WriteAllBytes(bad, data); Require(!store.TryImport(bad, out string error) && error != null);
            }
            Save(Pattern(), bad, new BmpBitmapEncoder()); Require(!store.TryImport(bad, out _), "Disguised BMP accepted");
            Require(!store.TryImport(Path.Combine(dir,"missing.jpg"), out _));
            Require(!store.TryImport("relative.png", out _) && !store.TryImport(store.ImagePath, out _));
            Require(before == store.Current && hash == Hash(File.ReadAllBytes(store.ImagePath)));
        }
        private static void Limits()
        {
            string dir = Dir(), source = Path.Combine(dir, "large.png"); var store = new TextureStore(Path.Combine(dir,"managed"));
            using (var f = File.Create(source)) f.SetLength(TextureStore.MaxFileBytes + 1);
            Require(!store.TryImport(source, out _));
            Save(Pattern(8193, 1, false), source); Require(!store.TryImport(source, out _));
            bool rejected = false;
            try { TextureStore.ValidateDimensions(6000,6000); } catch (InvalidDataException) { rejected = true; }
            Require(rejected);
            rejected = false;
            try { TextureStore.ValidateDimensions(8192,1); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Invisible extreme aspect ratio should be rejected");
            Save(Pattern(4096, 512, false), source); Require(store.TryImport(source, out string error), error);
            Require(store.Current.PixelWidth == 2048 && store.Current.PixelHeight == 256);
            Save(Pattern(8000,33,false),source); Require(store.TryImport(source,out error),error);
            Require(Math.Abs(store.Current.Width/store.Current.Height - 8000.0/33) < 0.00001, "Downsampling changed aspect");
        }
        private static void SaveFailures()
        {
            string dir = Dir(), source = Path.Combine(dir,"source.png"), managed = Path.Combine(dir,"managed"); Save(Pattern(), source);
            var store = new TextureStore(managed); Require(store.TryImport(source, out _));
            string hash = Hash(File.ReadAllBytes(store.ImagePath)); BitmapSource before = store.Current;
            using (var f = new FileStream(store.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(!store.TryImport(source, out string error) && error != null);
                Require(!store.TryClear(out error) && error != null);
            }
            Require(store.Current == before && hash == Hash(File.ReadAllBytes(store.ImagePath)));
            Require(Directory.GetFiles(managed,"*.tmp").Length == 0);
            string blocked = Path.Combine(dir,"blocked"); File.WriteAllText(blocked,"occupied");
            Require(!new TextureStore(blocked).TryImport(source, out _));
        }
        private static void LightingPersistence()
        {
            string dir = Dir(), source = Path.Combine(dir,"source.png"), managed = Path.Combine(dir,"managed"); Save(Pattern(), source);
            var store = new TextureStore(managed); Require(!store.FoldShading && store.TryImport(source, out _));
            Require(store.TrySetFoldShading(true, out _));
            string path = Path.Combine(managed,"fold-shading.txt"), hash = Hash(File.ReadAllBytes(path));
            using (var f = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) Require(!store.TrySetFoldShading(false,out _));
            Require(store.FoldShading && hash == Hash(File.ReadAllBytes(path)) && new TextureStore(managed).FoldShading);
            Require(store.TryClear(out _) && store.Current == null && new TextureStore(managed).Current == null && File.Exists(source));
            Require(store.TryImport(source,out _) && store.FoldShading, "New image reset lighting");
            Require(Directory.GetFiles(managed,"*.tmp").Length == 0);
        }
        private static void BadSavedFiles()
        {
            string dir = Dir(), image = Path.Combine(dir,"texture.image"), settings = Path.Combine(dir,"fold-shading.txt");
            File.WriteAllText(image,"broken"); File.WriteAllText(settings,"invalid");
            var store = new TextureStore(dir);
            Require(store.Current == null && store.LoadError != null && !store.FoldShading && store.HasStoredImage);
            Require(File.ReadAllText(image)=="broken" && File.ReadAllText(settings)=="invalid");
            Require(store.TryClear(out _) && !File.Exists(image));
        }
        private static void PlainImage()
        {
            var art = RugArt.RenderImage(400,300,1,1,Pattern(),false);
            Require(art.FacePx.SequenceEqual(art.BackPx) && art.FacePx.SequenceEqual(Pixels(art.Front)));
            for (int p = 0; p < art.FacePx.Length; p += 4)
                Require(art.FacePx[p] == 0 && Math.Abs(art.FacePx[p+1]+art.FacePx[p+2] - art.FacePx[p+3]) <= 1, "Decoration, tint or shadow added at " + p + ": " + string.Join(",",art.FacePx.Skip(p).Take(4)));
            Require(art.FacePx[(150*400+200)*4+3] == 0, "Hole filled");
            Save(art.Front,Path.Combine(_root,"image-flat.png")); Save(art.Roll,Path.Combine(_root,"image-roll.png"));
        }
        private static void ImageFit()
        {
            foreach (var size in new[] { (128,32),(32,128),(96,96),(512,16),(16,512),(8192,32),(32,8192) })
            {
                var art = RugArt.RenderImage(400,300,1,1,Pattern(size.Item1,size.Item2,false),false);
                int x0=400,y0=300,x1=-1,y1=-1;
                for(int y=0;y<300;y++) for(int x=0;x<400;x++) if(art.FacePx[(y*400+x)*4+3]>0)
                { x0=Math.Min(x0,x); x1=Math.Max(x1,x); y0=Math.Min(y0,y); y1=Math.Max(y1,y); }
                double scale=Math.Min(340.0/size.Item1,240.0/size.Item2);
                Require(Math.Abs(x1-x0+1-size.Item1*scale)<=2 && Math.Abs(y1-y0+1-size.Item2*scale)<=2, "Aspect/cropping " + size + ": " + (x0,y0,x1,y1) + ", scale=" + scale);
            }
        }
        private static void FlatTransition()
        {
            var art=RugArt.RenderImage(400,300,1,1,Pattern(340,240),false);
            var cloth=new Cloth(30,30,340,240,400,300,20);
            var renderer=new ClothRenderer(cloth,400,300,1,art.FacePx,art.BackPx,400,300,30,true,false);
            var dst=new byte[400*300*4]; renderer.Render(dst);
            Require(dst.Zip(art.FacePx,(a,b)=>Math.Abs(a-b)).Max()<=1,"Visible flat-to-drag change");
        }
        private static void FragmentDepth()
        {
            foreach(int alpha in new[] {1,64,127,128,129,254,255}) foreach(bool reverse in new[] {false,true})
            {
                var f=new SurfaceFragments(); f.Begin(1,0,1);
                void Low()=>f.Add(0,0,10,0,0,255,255,2);
                void High()=>f.Add(0,0,30,alpha,0,0,alpha,7);
                if(reverse){High();Low();}else{Low();High();}
                var dst=new byte[4]; var hits=new SurfaceHitMap(); hits.Begin(new Int32Rect(0,0,1,1)); f.Composite(dst,hits);
                Require(dst[0]==alpha && Math.Abs(dst[2]-(255-alpha))<=1 && dst[3]==255 && hits.NodeAt(0,0)==7);
            }
        }
        private static void AlphaBoundaries()
        {
            foreach(byte a in new byte[]{0,1,64,127,128,129,254,255})
            {
                var tex=new byte[80*60*4]; for(int p=0;p<tex.Length;p+=4){tex[p+2]=a;tex[p+3]=a;}
                var c=new Cloth(10,10,80,60,100,90,10); var r=new ClothRenderer(c,100,90,1,tex,tex,80,60,0,true,false);
                var dst=new byte[100*90*4]; var hits=new SurfaceHitMap(); var previous=new Int32Rect();
                r.Draw(dst,ref previous,c.X,c.Y,c.Z,null,hits); int p0=(30*100+30)*4;
                Require(Math.Abs(dst[p0+3]-a)<=1 && dst[p0]==0 && dst[p0+1]==0 && Math.Abs(dst[p0+2]-a)<=1);
                Require((hits.NodeAt(30,30)>=0)==(a>0) && dst[3]==0 && hits.NodeAt(0,0)<0);
            }
        }
        private static (Cloth c, byte[] tex) FoldFixture()
        {
            var tex=new byte[80*60*4];
            for(int y=0;y<60;y++) for(int x=0;x<80;x++)
            { int p=(y*80+x)*4; byte a=(byte)(x<40?255:128); if(x>=56&&x<=65&&y>=16&&y<=25)a=0; tex[p+(x<40?2:0)]=a; tex[p+3]=a; }
            var c=new Cloth(10,10,80,60,100,90,10);
            for(int i=0;i<c.Count;i++) if(c.X[i]>50){c.X[i]=100-c.X[i];c.Z[i]=25;}
            return(c,tex);
        }
        private static void FoldHole()
        {
            var(c,tex)=FoldFixture(); var r=new ClothRenderer(c,100,90,1,tex,tex,80,60,0,true,false);
            var dst=new byte[100*90*4];var hits=new SurfaceHitMap();var previous=new Int32Rect();r.Draw(dst,ref previous,c.X,c.Y,c.Z,null,hits);
            int hole=(30*100+30)*4, blend=(45*100+30)*4;
            Require(dst[hole]==0 && dst[hole+2]>=254 && hits.NodeAt(30,30)%c.Nx < c.Nx/2,"Hole did not pick lower red surface: " + string.Join(",",dst.Skip(hole).Take(4)) + ", nodeZ=" + c.Z[hits.NodeAt(30,30)]);
            Require(Math.Abs(dst[blend]-128)<=1 && Math.Abs(dst[blend+2]-127)<=1 && c.Z[hits.NodeAt(30,45)]==25,"Fold alpha/depth order");
            Require(r.BackFacingShare>0);
        }
        private static void LightingAlpha()
        {
            var(c,tex)=FoldFixture(); var off=new byte[100*90*4];var on=new byte[off.Length];
            new ClothRenderer(c,100,90,1,tex,tex,80,60,0,true,false).Render(off);
            new ClothRenderer(c,100,90,1,tex,tex,80,60,0,true,true).Render(on);
            Require(Alpha(off).SequenceEqual(Alpha(on)) && !off.SequenceEqual(on));
            var plain=RugArt.RenderImage(400,300,1,1,Pattern(),false);var lit=RugArt.RenderImage(400,300,1,1,Pattern(),true);
            Require(plain.FacePx.SequenceEqual(lit.FacePx) && plain.BackPx.SequenceEqual(lit.BackPx));
            Require(Alpha(Pixels(plain.Roll)).SequenceEqual(Alpha(Pixels(lit.Roll))) && !Pixels(plain.Roll).SequenceEqual(Pixels(lit.Roll)));
        }
        private static void TransparentImage()
        {
            var c=new Cloth(10,10,80,60,100,90,10);var tex=new byte[80*60*4];for(int i=0;i<c.Count;i++)c.Z[i]=30;
            foreach(bool lighting in new[]{false,true})
            {
                var dst=new byte[100*90*4];var hits=new SurfaceHitMap();var previous=new Int32Rect();
                new ClothRenderer(c,100,90,1,tex,tex,80,60,0,true,lighting).Draw(dst,ref previous,c.X,c.Y,c.Z,null,hits);
                Require(dst.All(b=>b==0) && hits.NodeAt(30,30)<0,"Transparent surface cast a shadow");
            }
        }
        private static void HitMapLifetime()
        {
            var hits=new SurfaceHitMap();hits.Begin(new Int32Rect(10,20,3,4));hits.Set(11,22,7);
            var shown=new SurfaceHitMap();shown.CopyFrom(hits);hits.Begin(new Int32Rect(50,50,2,2));
            Require(shown.NodeAt(11,22)==6 && hits.NodeAt(11,22)<0 && shown.NodeAt(9,20)<0);
        }
        private static void EngineCapture()
        {
            var(c,tex)=FoldFixture();
            using var e=new ClothEngine(c,100,90,1,tex,tex,80,60,0,Dispatcher.CurrentDispatcher,(r,p)=>{},true,false);
            e.ShowNow();Require(!e.HasSurfaceAt(95,80) && !e.Grab(95,80));
            Require(e.HasSurfaceAt(30,30) && e.Grab(30,30));e.MoveHand(95,80);Require(c.IsHeld);e.Release();Require(!c.IsHeld);
        }
        private static void StaleSurfaceGrab()
        {
            var c=new Cloth(10,10,80,60,300,100,10);int selected=0;
            // Move the intact surface away from the displayed frame. A single
            // teleported corner would start with self-intersections and velocity.
            for(int i=0;i<c.Count;i++)c.X[i]+=100;
            c.Freeze();
            Require(c.Grab(10,10,selected));float before=c.X[selected];c.MoveHand(40,10);c.Step(1f/60);Require(c.IsHeld && c.X[selected]!=before);
            c.Release();Require(!c.Grab(10,10,c.Count));
        }
    }
}
