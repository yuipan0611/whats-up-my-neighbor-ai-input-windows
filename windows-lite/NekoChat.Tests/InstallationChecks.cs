using System.IO.Compression;
using System.Text.Json;
using NekoChat.Core;

internal static class InstallationChecks
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool value,string message){checks++;if(!value)throw new Exception("Upgrade cleanup: "+message);}
        void Reject(Action action,string message)
        {
            bool rejected=false;
            try{action();}catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException){rejected=true;}
            Check(rejected,message);
        }
        MemoryStream Payload(string version, bool obsolete=false)
        {
            var result=new MemoryStream();
            using(var zip=new ZipArchive(result,ZipArchiveMode.Create,true))
                foreach(var name in PayloadArchive.RequiredFiles.Concat(new[]{"Assets/cat.png","Coats/custom.json"}).Concat(obsolete?new[]{"obsolete.dll"}:[]))
                {using var text=new StreamWriter(zip.CreateEntry(name).Open());text.Write(version+":"+name);}
            result.Position=0;return result;
        }
        var root=Path.Combine(Path.GetTempPath(),"nekochat-upgrade-checks-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Parent(string test)=>Path.Combine(root,test,"Programs");
        string Recovery(string test)=>Path.Combine(root,test,"Data","UpgradePreservedFiles");
        string Target(string test)=>Path.Combine(Parent(test),"NekoChat.WindowsLite");
        string[] Backups(string test)=>Directory.GetDirectories(Parent(test),".nekochat-previous-*");
        string Backup(string test)=>Path.Combine(Parent(test),".nekochat-previous-20261028010101-"+Guid.NewGuid().ToString("N"));
        void Fresh(string test,string version="one",bool obsolete=false)
        {using var zip=Payload(version,obsolete);using var update=InstallationUpdate.Begin(zip,Parent(test),Recovery(test));update.AcceptLaunch(_=>{});}
        try
        {
            Fresh("repeat","one",obsolete:true);
            var target=Target("repeat");
            File.WriteAllText(Path.Combine(target,"user-artwork.png"),"my-art");
            File.WriteAllText(Path.Combine(target,"Assets","cat.png"),"my-custom-cat");
            File.WriteAllText(Path.Combine(target,"coreclr.dll"),"user-modified-program");
            var settings=Path.Combine(root,"repeat","Data","settings.json");Directory.CreateDirectory(Path.GetDirectoryName(settings)!);File.WriteAllText(settings,"keep-settings");
            for(int n=0;n<3;n++)
            {
                using var zip=Payload("next-"+n);
                using var update=InstallationUpdate.Begin(zip,Parent("repeat"),Recovery("repeat"));
                Check(Backups("repeat").Length==1,"previous app kept before launch");
                var result=update.AcceptLaunch(folder=>Check(File.ReadAllText(Path.Combine(folder,"NekoChat.Windows.exe"))=="next-"+n+":NekoChat.Windows.exe","new launch sees verified replacement"));
                Check(result.Warnings.Count==0 && Backups("repeat").Length==0,"success and repeated upgrades remove old app folders");
            }
            Check(!File.Exists(Path.Combine(target,"obsolete.dll")),"obsolete managed library is gone");
            Check(Directory.GetFiles(Recovery("repeat"),"user-artwork.png",SearchOption.AllDirectories).Select(File.ReadAllText).SequenceEqual(new[]{"my-art"}),"unknown artwork preserved once");
            Check(Directory.GetFiles(Recovery("repeat"),"cat.png",SearchOption.AllDirectories).Select(File.ReadAllText).SequenceEqual(new[]{"my-custom-cat"}),"modified bundled artwork preserved");
            Check(Directory.GetFiles(Recovery("repeat"),"coreclr.dll",SearchOption.AllDirectories).Select(File.ReadAllText).SequenceEqual(new[]{"user-modified-program"}),"manifest-modified code is retained too");
            Check(File.ReadAllText(settings)=="keep-settings","settings untouched");

            foreach(var reason in new[]{"launch","cancel","tamper"})
            {
                Fresh(reason);
                using(var zip=Payload("two"))
                using(var update=InstallationUpdate.Begin(zip,Parent(reason),Recovery(reason)))
                {
                    if(reason=="launch")Reject(()=>update.AcceptLaunch(_=>throw new IOException("launch failed")),"failed launch rolls back");
                    if(reason=="tamper")
                    {
                        File.WriteAllText(Path.Combine(update.Destination,"NekoChat.Windows.exe"),"modified");
                        Reject(()=>update.AcceptLaunch(_=>throw new Exception("must not launch")),"post-copy tampering rejected before launch");
                    }
                }
                Check(File.ReadAllText(Path.Combine(Target(reason),"NekoChat.Windows.exe"))=="one:NekoChat.Windows.exe","failure/cancel restores prior executable");
                Check(Backups(reason).Length==0,"restored app is not deleted or stranded as a backup");
            }
            using(var zip=Payload("fresh-failure"))
            using(var update=InstallationUpdate.Begin(zip,Parent("fresh-failure"),Recovery("fresh-failure")))
                Reject(()=>update.AcceptLaunch(_=>throw new IOException("launch failed")),"fresh launch failure handled");
            Check(!Directory.Exists(Target("fresh-failure")),"failed fresh install is removed");

            // Migrate beta.7-style folders without manifests, preserving unknown/changed resources.
            Directory.CreateDirectory(Parent("legacy"));
            using(var zip=Payload("legacy"))PayloadArchive.Extract(zip,Target("legacy"));
            var historic=Backup("legacy");
            using(var zip=Payload("legacy"))PayloadArchive.Extract(zip,historic);
            File.WriteAllText(Path.Combine(historic,"custom-note.txt"),"retain-historic");
            var unrelated=Path.Combine(Parent("legacy"),".nekochat-previous-unrelated");Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated,"data.txt"),"untouched");
            using(var zip=Payload("new"))
            using(var update=InstallationUpdate.Begin(zip,Parent("legacy"),Recovery("legacy")))
            {
                var result=update.AcceptLaunch(_=>{});
                Check(result.Warnings.Count==0 && !Directory.Exists(historic),"legacy completed backups cleaned");
                Check(result.PreservedFiles>=3,"legacy custom and changed artwork preserved");
            }
            Check(File.ReadAllText(Path.Combine(unrelated,"data.txt"))=="untouched","unrelated backup-shaped directory retained");
            Check(Directory.GetFiles(Recovery("legacy"),"custom-note.txt",SearchOption.AllDirectories).Length==1,"historic custom files retained");
            Check(Directory.GetFiles(Recovery("legacy"),"cat.png",SearchOption.AllDirectories).Length==1,"identical custom/legacy resource recovery deduplicates");

            Fresh("locked");
            using(var zip=Payload("two"))
            using(var update=InstallationUpdate.Begin(zip,Parent("locked"),Recovery("locked")))
            {
                var prior=Backups("locked").Single();
                using(var held=new FileStream(Path.Combine(prior,"NekoChat.Windows.exe"),FileMode.Open,FileAccess.Read,FileShare.None))
                {
                    var result=update.AcceptLaunch(_=>{});
                    Check(result.Warnings.Count==1 && Directory.Exists(prior),"locked cleanup is reported and remaining backup retained");
                    Check(File.ReadAllText(Path.Combine(update.Destination,"NekoChat.Windows.exe"))=="two:NekoChat.Windows.exe","cleanup failure keeps the successful new install");
                }
            }
            using(var zip=Payload("three"))
            using(var update=InstallationUpdate.Begin(zip,Parent("locked"),Recovery("locked")))
                Check(update.AcceptLaunch(_=>{}).Warnings.Count==0 && Backups("locked").Length==0,"later successful install retries leftover cleanup");

            Fresh("preserve-failure");
            File.WriteAllText(Path.Combine(Target("preserve-failure"),"personal.txt"),"keep-original");
            Directory.CreateDirectory(Path.GetDirectoryName(Recovery("preserve-failure"))!);
            File.WriteAllText(Recovery("preserve-failure"),"unrelated-file-blocking-recovery");
            using(var zip=Payload("two"))
            using(var update=InstallationUpdate.Begin(zip,Parent("preserve-failure"),Recovery("preserve-failure")))
            {
                var result=update.AcceptLaunch(_=>{});
                Check(result.Warnings.Count==1,"recovery copy failure is visible");
                Check(File.ReadAllText(Path.Combine(Backups("preserve-failure").Single(),"personal.txt"))=="keep-original","nothing unowned deleted before verified copy");
            }

            Fresh("concurrent");
            using(var first=Payload("two"))
            using(var update=InstallationUpdate.Begin(first,Parent("concurrent"),Recovery("concurrent")))
            using(var second=Payload("three"))
                Reject(()=>InstallationUpdate.Begin(second,Parent("concurrent"),Recovery("concurrent")),"concurrent replacement rejected");
            Check(File.ReadAllText(Path.Combine(Target("concurrent"),"NekoChat.Windows.exe"))=="one:NekoChat.Windows.exe","lock rejection did not damage original");

            foreach(var test in new[]{"unknown","file","bad-payload"})
            {
                Directory.CreateDirectory(Parent(test));
                if(test=="file")File.WriteAllText(Target(test),"unrelated");
                else{Directory.CreateDirectory(Target(test));File.WriteAllText(Path.Combine(Target(test),"personal.txt"),"unrelated");}
                using var zip=test=="bad-payload"?new MemoryStream(new byte[]{1,2,3}):Payload("new");
                Reject(()=>InstallationUpdate.Begin(zip,Parent(test),Recovery(test)),"unknown targets or invalid package protected");
                Check(test=="file"?File.ReadAllText(Target(test))=="unrelated":File.ReadAllText(Path.Combine(Target(test),"personal.txt"))=="unrelated","unrelated destination untouched");
            }
            Fresh("bad-existing");
            using(var zip=new MemoryStream(new byte[]{1,2,3}))Reject(()=>InstallationUpdate.Begin(zip,Parent("bad-existing"),Recovery("bad-existing")),"bad archive never moves working app");
            Check(File.ReadAllText(Path.Combine(Target("bad-existing"),"NekoChat.Windows.exe"))=="one:NekoChat.Windows.exe","bad package preserved app with ownership manifest");

            // Reparse points are rejected before scanning or deleting their targets.
            Fresh("links");
            var outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"private.txt"),"private");
            var linked=Backup("links");
            bool linkedCreated=false;
            try{Directory.CreateSymbolicLink(linked,outside);linkedCreated=true;}
            catch(UnauthorizedAccessException)when(OperatingSystem.IsWindows()){Console.WriteLine("Upgrade cleanup: symbolic-link creation unavailable on this Windows token.");}
            if(linkedCreated)
            {
                using var zip=Payload("two");using var update=InstallationUpdate.Begin(zip,Parent("links"),Recovery("links"));
                Check(update.AcceptLaunch(_=>{}).Warnings.Count==1,"linked backup skipped and reported");
                Check(File.ReadAllText(Path.Combine(outside,"private.txt"))=="private","external linked data untouched");
                Directory.Delete(linked);
            }
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine($"Upgrade cleanup: {checks} replacement, rollback, preservation and boundary checks passed.");
        return checks;
    }
}
