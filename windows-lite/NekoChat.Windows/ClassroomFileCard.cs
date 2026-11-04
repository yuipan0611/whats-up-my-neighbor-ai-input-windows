using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NekoChat.Core.ClassroomFiles;
namespace NekoChat.Windows;

internal sealed class ClassroomFileView(SharedFile row,string? path,Func<string,Task> action):INotifyPropertyChanged
{
    public SharedFile Row {get;}=row;
    public string? Path {get;}=path;
    public Func<string,Task> Action {get;}=action;
    private int _progress;
    public int Progress {get=>_progress;set{_progress=value;PropertyChanged?.Invoke(this,new(nameof(Progress)));}}
    public string? Error {get;set;}
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()=>PropertyChanged?.Invoke(this,new(""));
}
public sealed class ClassroomFileCard:UserControl
{
    public object File{get=>GetValue(FileProperty);set=>SetValue(FileProperty,value);}
    public static readonly DependencyProperty FileProperty=DependencyProperty.Register(nameof(File),typeof(object),typeof(ClassroomFileCard),new PropertyMetadata(null,(d,e)=>((ClassroomFileCard)d).Observe(e.NewValue)));
    private ClassroomFileView? _observed;
    private void Observe(object value){if(_observed is not null)_observed.PropertyChanged-=Changed;_observed=value as ClassroomFileView;if(_observed is not null){_observed.PropertyChanged+=Changed;Build(_observed);}}
    private void Changed(object? sender,PropertyChangedEventArgs e){if(_observed is {} row)Build(row);}
    private void Build(ClassroomFileView view)
    {
        var f=view.Row.File;var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new(40)});grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        grid.Children.Add(new Border{Width=34,Height=42,CornerRadius=new(6),Background=new SolidColorBrush(Color.FromRgb(237,242,251)),Child=new TextBlock{Text=System.IO.Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant(),FontSize=10,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center}});
        var text=new StackPanel{Margin=new(10,0,12,0),VerticalAlignment=VerticalAlignment.Center};
        text.Children.Add(new TextBlock{Text=f.Name,ToolTip=f.Name,FontWeight=FontWeights.SemiBold,FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis});
        string owner=view.Row.Owned?"你分享":f.UploaderIp+" 分享";
        text.Children.Add(new TextBlock{Text=ChatWindow.AttachmentSize(f.Size)+" · "+owner,FontSize=11,Foreground=Brushes.SlateGray,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)});
        string state=view.Path is not null?"已在本機":view.Row.State switch{"receiving"=>"下載中 "+view.Progress+"%","cancelled"=>"已取消 · 可重試","failed"=>view.Row.Problem switch{"storage_full"=>"空間已滿，請清理後重試","hash_mismatch"=>"內容校驗失敗 · 請重試",_=>"下載失敗 · 請重試"},_=>"可下載"};
        state+=view.Row.Replicas>0?" · 已確認 "+view.Row.Replicas+" 個在線副本":" · 尚未確認在線副本";
        if(view.Row.Owned&&view.Row.Replicas==0)state+="\n尚無在線備援，請保持 NekoChat 開啟";
        text.Children.Add(new TextBlock{Text=view.Error??state,Foreground=view.Error is null?Brushes.SlateGray:Brushes.Firebrick,FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new(0,3,0,0)});
        if(f.Note.Length>0)text.Children.Add(new TextBlock{Text=f.Note,Foreground=Brushes.SlateGray,FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)});
        Grid.SetColumn(text,1);grid.Children.Add(text);var actions=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        void Add(string label,string op,bool danger=false)
        {
            var b=new Button{Content=label,FontSize=12,MinWidth=60,Padding=new(8,4,8,4),Margin=new(0,0,0,4),Foreground=danger?Brushes.Firebrick:Brushes.Black};
            System.Windows.Automation.AutomationProperties.SetName(b,label+f.Name);
            b.Click+=async(_,_)=>{b.IsEnabled=false;try{await view.Action(op);}finally{b.IsEnabled=true;}};actions.Children.Add(b);
        }
        Add(view.Row.State=="receiving"?"取消":view.Path is null?(view.Row.State is "failed" or "cancelled"?"重試":"下載"):"另存…",view.Row.State=="receiving"?"cancel":view.Path is null?"download":"save");
        Add("分享資訊","info");if(view.Row.Owned)Add("撤下…","withdraw",true);Grid.SetColumn(actions,2);grid.Children.Add(actions);
        Content=new Border{Child=grid,Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(227,232,239)),BorderThickness=new(1),CornerRadius=new(10),Padding=new(14),Margin=new(0,0,0,10)};
    }
}
