using System.Net;
using System.Text;
using System.Text.Json;
using NekoChat.Core;

internal static class ClassroomGroupChecks
{
    internal static async Task<int> Run()
    {
        int checks=0;
        void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
        void Reject(Action action,string message){bool failed=false;try{action();}catch(Exception ex)when(ex is InvalidDataException or JsonException){failed=true;}Check(failed,message);}
        foreach(var ip in new[]{"10.0.0.7","172.16.2.3","172.31.255.254","192.168.1.23","127.0.0.2"})
            Check(ClassroomInvitation.IPForUser(ClassroomInvitation.UserForIP(ip))==ip,"classroom IP addressing roundtrip "+ip);
        foreach(var ip in new[]{"8.8.8.8","0.0.0.0","169.254.1.1","192.168.01.23","127.1","::1","192.168.1.23:8765","192.168.1.23.evil",""})
            Reject(()=>ClassroomInvitation.UserForIP(ip),"invalid recipient IP "+ip);
        Reject(()=>ClassroomInvitation.IPForUser("alice"),"manual user ID is not a classroom IP");
        var invite=new ClassroomInvitation(1,new string('a',32),["192.168.1.10"],8765,LanInvitation.NewToken());
        var decoded=ClassroomInvitation.Parse(invite.Encode());
        Check(decoded.RoomId==invite.RoomId && decoded.Token==invite.Token && decoded.Hosts.SequenceEqual(invite.Hosts),"shared classroom invitation roundtrip");
        Check(!invite.ToString().Contains(invite.Token),"invitation debug representation redacts capability");
        foreach(var invalid in new[]{invite with{Version=2},invite with{Hosts=[]},invite with{Hosts=["8.8.8.8"]},
            invite with{Hosts=["10.0.0.1","10.0.0.1"]},invite with{Port=0},invite with{Port=65536},invite with{RoomId="x"},invite with{Token="x"}})
            Reject(()=>invalid.Encode(),"invalid classroom capability");
        Reject(()=>ClassroomInvitation.Parse("NEKO1-paired-room"),"old paired invitation requires explicit old client");
        Reject(()=>ClassroomInvitation.Parse("NEKOCLASS1-"+new string('x',5000)),"classroom invitation bound");
        var json=JsonSerializer.Serialize(invite,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        string Encode(string value)=>"NEKOCLASS1-"+Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+','-').Replace('/','_');
        Reject(()=>ClassroomInvitation.Parse(Encode(json.Replace("\"version\":1","\"version\":1,\"version\":1"))),"duplicate classroom fields");
        Reject(()=>ClassroomInvitation.Parse(Encode(json.Replace("\"version\":1","\"version\":1,\"command\":\"paste\""))),"no commands in classroom invitations");
        var handler=new AdmissionHandler(invite);using var http=new HttpClient(handler);
        var endpoint=await invite.FindEndpointAsync(http,CancellationToken.None);
        Check(handler.DiscoveryWasPrivate && handler.Calls==2,"classroom discovery is bounded and credential-free");
        var session=await invite.JoinAsync(http,endpoint,CancellationToken.None);
        Check(handler.AdmissionWasPrivate && session.User=="ip-192-168-1-21" && session.TeacherIP=="192.168.1.10","individual admission and teacher address");
        Check(session.Token!=invite.Token && !session.ToString().Contains(session.Token),"individual token is distinct and redacted");
        async Task RejectJoin(string message){bool rejected=false;try{await invite.JoinAsync(http,endpoint,CancellationToken.None);}catch(InvalidDataException){rejected=true;}Check(rejected,message);}
        string original=handler.Response;
        foreach(var bad in new[]{
            original.Replace("\"version\":1","\"version\":2"),
            original.Replace("\"version\":1","\"version\":1,\"action\":\"paste\""),
            original.Replace("192.168.1.21","8.8.8.8"),
            original.Replace("192.168.1.10","192.168.1.99"),
            original.Replace(invite.RoomId,new string('b',32)),
            original.Replace("ip-192-168-1-21","ip-192-168-1-22"),
            original.Replace("\"version\":1","\"version\":1,\"version\":1")
        }){handler.Response=bad;await RejectJoin("reject invalid admission response");}
        handler.Response=original;handler.Status=HttpStatusCode.Conflict;
        await RejectJoin("duplicate-IP enrollment fails clearly");
        int calls=handler.Calls;bool wrongHost=false;
        try{await invite.JoinAsync(http,new Uri("ws://192.168.1.99:8765/nekochat/v1/ws"),CancellationToken.None);}catch(InvalidDataException){wrongHost=true;}
        Check(wrongHost && handler.Calls==calls,"capability is never sent to a host outside invitation");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
        try{await invite.FindEndpointAsync(http,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        Check(stopped,"classroom discovery cancellation");
        return checks;
    }
    private sealed class AdmissionHandler:HttpMessageHandler
    {
        private readonly ClassroomInvitation invitation;
        internal string Response;
        internal HttpStatusCode Status=HttpStatusCode.OK;
        internal int Calls;
        internal bool DiscoveryWasPrivate=true,AdmissionWasPrivate;
        internal AdmissionHandler(ClassroomInvitation invitation)
        {
            this.invitation=invitation;
            Response=JsonSerializer.Serialize(new{protocol="nekochat",version=1,room_id=invitation.RoomId,user="ip-192-168-1-21",ip="192.168.1.21",teacher_ip="192.168.1.10",token=LanInvitation.NewToken()});
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Calls++;
            if(request.Method==HttpMethod.Get){
                DiscoveryWasPrivate&=request.Headers.Authorization is null && request.RequestUri!.Query=="";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{protocol="nekochat",version=1,room_id=invitation.RoomId,room_mode="classroom",admission_version=1}))});
            }
            AdmissionWasPrivate=request.RequestUri!.AbsolutePath=="/nekochat/v1/classroom/join" && request.RequestUri.Query=="" &&
                request.Headers.Authorization?.ToString()=="Bearer "+invitation.Token && request.Headers.GetValues("X-NekoChat-Room").Single()==invitation.RoomId && request.Content is null;
            return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent(Response)});
        }
    }
}
