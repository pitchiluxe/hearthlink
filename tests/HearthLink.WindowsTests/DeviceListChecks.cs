using HearthLink;
using System.Reflection;

static class DeviceListChecks
{
    public static async Task Run(MainWindow owner,Action<bool,string> check)
    {
        await owner.OnUi(()=>
        {
            var list=(DeviceList)typeof(MainWindow).GetField("hosts",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
            var item=new ListViewItem(["Paint fixture","192.0.2.1","Online","Today 10:21 AM"]);
            list.Items.Add(item);
            try
            {
                var row=new Rectangle(0,0,list.Columns.Cast<ColumnHeader>().Sum(c=>c.Width),30);
                using var image=new Bitmap(row.Width,row.Height);
                using var graphics=Graphics.FromImage(image);
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                void PaintItem()=>typeof(ListView).GetMethod("OnDrawItem",flags)!.Invoke(list,[new DrawListViewItemEventArgs(graphics,item,row,item.Index,ListViewItemStates.Default)]);
                graphics.Clear(Color.Magenta);
                PaintItem();
                check(image.GetPixel(2,10).ToArgb()==Color.Magenta.ToArgb(),"hover-only DrawItem does not erase or redraw the status row");
                int x=0;
                for(int column=0;column<4;column++)
                {
                    var cell=new Rectangle(x,0,list.Columns[column].Width,30);
                    graphics.SetClip(cell);
                    typeof(ListView).GetMethod("OnDrawSubItem",flags)!.Invoke(list,[new DrawListViewSubItemEventArgs(graphics,cell,item,item.SubItems[column],item.Index,column,list.Columns[column],ListViewItemStates.Default)]);
                    graphics.ResetClip();
                    check(image.GetPixel(x+2,10).ToArgb()!=Color.Magenta.ToArgb(),$"cell {column} paints independently within its clip");
                    x+=cell.Width;
                }
                using var before=(Bitmap)image.Clone();
                for(int hover=0;hover<20;hover++) PaintItem();
                bool unchanged=true;
                for(int y=0;y<image.Height;y++) for(int px=0;px<image.Width;px++)
                    unchanged &= image.GetPixel(px,y)==before.GetPixel(px,y);
                check(unchanged,"repeated hover-only events leave stripe and all four cells unchanged");
            }
            finally {list.Items.Remove(item);}
            return true;
        });
    }
}
