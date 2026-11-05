using NekoChat.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using NekoChat.Core.Attachments;
using NekoChat.Core.ClassroomFiles;
namespace NekoChat.Windows;

public partial class ChatWindow
{
    private readonly ObservableCollection<ClassroomFileView> _sharedFiles=[];
    private long? _shareBefore;
    private string? _uploadPath;
    private CancellationTokenSource? _uploadCancellation;
    private readonly Dictionary<string,int> _shareProgress=[];
    internal void ShowShareProgress(string id,long done,long total)
    {
        _shareProgress[id]=total==0?100:(int)(done*100/total);
        foreach(var row in _sharedFiles.Where(f=>f.Row.File.Id==id))row.Progress=_shareProgress[id];
    }
    private async Task LoadSharedFilesAsync(bool older)
    {
        if(!_showTeacherFiles||_runtime?.ClassroomFiles is not {} service)return;
        var runtime=_runtime;long request=++_teacherFilesRequest;MoreTeacherFilesButton.IsEnabled=false;
        try
        {
            long? before=older?_shareBefore:null;var rows=await Task.Run(()=>service.List(before));
            if(request!=_teacherFilesRequest||!_showTeacherFiles||_runtime!=runtime||_stopping)return;
            if(!older)_sharedFiles.Clear();
            foreach(var row in rows)
            {
                if(_sharedFiles.Any(f=>f.Row.File.Id==row.File.Id))continue;
                var view=new ClassroomFileView(row,service.LocalPath(row),op=>SharedFileActionAsync(row.File.Id,op));view.Progress=_shareProgress.GetValueOrDefault(row.File.Id);_sharedFiles.Add(view);
            }
            _shareBefore=rows.Count==50?rows[^1].Sequence:null;
            MoreTeacherFilesButton.Visibility=_shareBefore is null?Visibility.Collapsed:Visibility.Visible;
            TeacherFilesState.Text="還沒有課堂檔案。\n老師與同學都可以上傳分享。";TeacherFilesState.Visibility=_sharedFiles.Count==0?Visibility.Visible:Visibility.Collapsed;
            ClassroomHostState.Text=service.HostStatus;
        }
        catch(Exception ex){if(request==_teacherFilesRequest){TeacherFilesState.Text="無法讀取課堂檔案，請重新開啟。";TeacherFilesState.Visibility=Visibility.Visible;ShowStorageError(ex);}}
        finally{if(request==_teacherFilesRequest)MoreTeacherFilesButton.IsEnabled=true;}
    }
    private async void UploadClassroomFileClick(object sender,RoutedEventArgs e)
    {
        if(_runtime?.ClassroomFiles is null||_uploadCancellation is not null)return;
        var pick=new Microsoft.Win32.OpenFileDialog{Title="分享檔案給全班",Multiselect=false,Filter="所有檔案|*.*"};
        if(pick.ShowDialog(this)!=true)return;
        await AddClassroomUploadPathsAsync(new[] { pick.FileName });
    }
    private bool CanDropClassroomFile() => _showTeacherFiles && !_stopping && !_closingRequested && _runtime?.ClassroomFiles is not null && _uploadCancellation is null && _uploadPath is null;
    private async Task AddClassroomUploadPathsAsync(string[] paths)
    {
        if (!CanDropClassroomFile()) return;
        if (paths.Length != 1)
        {
            ShareUploadPanel.Visibility = Visibility.Visible;
            ShareUploadState.Text = "課堂檔案一次只能分享一個檔案，請重新拖入。";
            RetryClassroomUpload.Visibility = Visibility.Collapsed;
            return;
        }
        _uploadPath = paths[0];
        await UploadClassroomFileAsync();
    }
    private async void RetryClassroomUploadClick(object sender,RoutedEventArgs e)=>await UploadClassroomFileAsync();
    private void CancelClassroomUploadClick(object sender,RoutedEventArgs e){if(_uploadCancellation is not null)_uploadCancellation.Cancel();else{_uploadPath=null;ShareUploadPanel.Visibility=Visibility.Collapsed;UploadClassroomFileButton.IsEnabled=true;}}
    private async Task UploadClassroomFileAsync()
    {
        if(_runtime?.ClassroomFiles is not {} service||_uploadPath is null||_uploadCancellation is not null)return;
        _uploadCancellation=CancellationTokenSource.CreateLinkedTokenSource(_runtime.Lifetime);
        ShareUploadPanel.Visibility=Visibility.Visible;ShareUploadState.Text=Path.GetFileName(_uploadPath)+" · 準備分享中…";RetryClassroomUpload.Visibility=Visibility.Collapsed;UploadClassroomFileButton.IsEnabled=false;
        try{await service.ShareAsync(_uploadPath,cancellation:_uploadCancellation.Token);_uploadPath=null;ShareUploadPanel.Visibility=Visibility.Collapsed;await LoadSharedFilesAsync(false);}
        catch(Exception ex){_runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: ex);ShareUploadState.Text=Path.GetFileName(_uploadPath)+" · "+AttachmentException.Describe(ex);RetryClassroomUpload.Visibility=Visibility.Visible;}
        finally{_uploadCancellation.Dispose();_uploadCancellation=null;UploadClassroomFileButton.IsEnabled=_uploadPath is null;}
    }
    private async Task SharedFileActionAsync(string id,string action)
    {
        if(_runtime?.ClassroomFiles is not {} service)return;
        var view=_sharedFiles.FirstOrDefault(v=>v.Row.File.Id==id);if(view is null)return;
        try
        {
            if(action=="cancel")service.Cancel(id);
            else if(action=="download")await service.DownloadAsync(id,_runtime.Lifetime);
            else if(action=="withdraw")
            {
                if(!view.Row.Owned)return;
                if(MessageBox.Show(this,"撤下「"+view.Row.File.Name+"」的分享？\n同步後停止提供下載。離線節點回線後才會同步；別人已下載或另存的檔案仍會保留。","撤下分享",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
                await service.WithdrawAsync(id,_runtime.Lifetime);
            }
            else if(action=="info")
            {
                MessageBox.Show(this,view.Row.File.Name+"\n分享者 IP："+view.Row.File.UploaderIp+"\n時間："+view.Row.File.CreatedUtc+"\nSHA-256："+view.Row.File.Sha256+"\n在線副本："+view.Row.Replicas+"\n分享者 IP 是顯示資訊；撤下以操作憑證驗證。","分享資訊");return;
            }
            else if(action=="save")
            {
                var pick=new Microsoft.Win32.SaveFileDialog{Title="另存課堂檔案",FileName=view.Row.File.Name,Filter="所有檔案|*.*",OverwritePrompt=true};if(pick.ShowDialog(this)!=true)return;
                await using var source=new FileStream(service.LocalPath(view.Row)??throw new AttachmentException("unavailable"),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);
                if(Convert.ToHexStringLower(await SHA256.HashDataAsync(source,_runtime.Lifetime))!=view.Row.File.Sha256)throw new AttachmentException("hash_mismatch");source.Position=0;
                await using var output=new FileStream(pick.FileName,FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);await source.CopyToAsync(output,_runtime.Lifetime);await output.FlushAsync(_runtime.Lifetime);return;
            }
            await LoadSharedFilesAsync(false);
        }
        catch(Exception ex){_runtime?.Diagnostics.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: ex);view.Error=AttachmentException.Describe(ex);view.Refresh();}
    }
}
