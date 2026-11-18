// 卸载时删除用户数据目录。
//
// 由 MSI 以 deferred 方式调用（Impersonate=no），路径经 CustomActionData 传进来，
// 多个用 | 分隔：%AppData%\AstraBrew Launcher | %LocalAppData%\AstraBrew Launcher。
//
// 为什么不用 util:RemoveFolderEx 或内联脚本：
//   · RemoveFolderEx 在卸载阶段取不到目录属性（报 Missing folder property），
//     只有安装阶段才解析得到；
//   · WiX 5 不再支持内联脚本，且 VBScript 已被微软标记为弃用功能。
// 这里只做一件事：把两棵目录删掉，删不掉就跳过——绝不因为删数据失败而让卸载报错。
function RemoveUserData() {
    var fso;
    try {
        fso = new ActiveXObject("Scripting.FileSystemObject");
    } catch (e) {
        return;
    }

    var paths = Session.Property("CustomActionData");
    if (!paths) {
        return;
    }

    var list = paths.split("|");
    for (var i = 0; i < list.length; i++) {
        var path = list[i];
        if (!path) {
            continue;
        }

        try {
            if (fso.FolderExists(path)) {
                fso.DeleteFolder(path, true);
            }
        } catch (e) {
            // 目录被占用、无权限：留着，别让卸载失败
        }
    }
}
