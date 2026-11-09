using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;
using NekoChat.TestFixtures;

namespace NekoChat.Tests;
internal static class ContactsUiSeedChecks
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"contacts-profile-"+Guid.NewGuid().ToString("N"));
        int count=0;void Check(bool ok,string label){count++;if(!ok)throw new InvalidOperationException(label);}
        void Guard(string path){if(path!=root)throw new InvalidOperationException("Fixture root mismatch.");}
        try
        {
            ContactsUiSmokeSeed.CreateV13(root,Guard);
            string db=Path.Combine(root,"data","nekochat.db");
            using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
            {c.Open();using var q=c.CreateCommand();q.CommandText="PRAGMA user_version";Check(Convert.ToInt64(q.ExecuteScalar())==13,"native fixture really starts at v13");}
            byte[] original=NekoChat.TestFixtures.TestFixtureFiles.ReadAllBytes(db);bool refused=false;
            try{ContactsUiSmokeSeed.CreateV13(root,Guard);}catch(InvalidOperationException){refused=true;}
            Check(refused&&NekoChat.TestFixtures.TestFixtureFiles.ReadAllBytes(db).SequenceEqual(original),"seed cannot overwrite an existing profile");
            using var upgraded=ChatStore.Open(new(Path.Combine(root,"data")));
            Check(upgraded.GetRosterSnapshot().Count==0,"upgrade keeps old unknown history until a new receive triggers contact admission");
            upgraded.ReceiveClassroomMessage(ContactsUiSmokeSeed.OldIp,new(Guid.NewGuid().ToString("D"),"合成升級後來訊"),()=>true);
            var row=upgraded.GetRosterSnapshot().Single();var p=ClassroomPrincipalKey.ForRosterEntry(row.Entry.Id);
            Check(row.Source=="auto"&&row.Entry.Endpoint==ContactsUiSmokeSeed.OldIp,"real post-upgrade receive merges unknown history into one auto contact");
            Check(upgraded.ListClassroomMessages(p).Items.Count==2&&upgraded.ListClassroomMessages(p).Items.Any(m=>m.RawText=="合成舊版歷史：保留這則訊息")&&upgraded.GetClassroomDraftState(p).RawText=="合成升級前草稿","shared fixture preserves actual old history/draft");
            Check(upgraded.GetReadReceiptPolicy().Enabled&&!upgraded.GetReadReceiptPreferences().NoticeShown,"real upgrade requests the one-time native notice");
            Check(upgraded.ListBackups().Any(b=>b.FileName.Contains("-v13-",StringComparison.Ordinal)),"real v13 pre-upgrade backup exists");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine($"Contacts UI seed: {count} real SQLite migration/isolation checks passed; no WPF acceptance claimed.");
    }
}
