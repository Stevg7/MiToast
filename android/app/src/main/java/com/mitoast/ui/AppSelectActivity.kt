package com.mitoast.ui

import android.content.Context
import android.content.pm.ApplicationInfo
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.Drawable
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.mitoast.MiToastTheme
import com.mitoast.PageBg
import com.mitoast.BrandOrange
import com.mitoast.TextDark
import com.mitoast.TextGray
import com.mitoast.prefs.AppWhitelistManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class AppSelectActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MiToastTheme {
                AppSelectScreen(onBack = { finish() })
            }
        }
    }
}

private data class InstalledApp(
    val packageName: String,
    val label: String,
    val isSystem: Boolean,
    val icon: Bitmap?
)

@Composable
private fun AppSelectScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var apps by remember { mutableStateOf<List<InstalledApp>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var query by remember { mutableStateOf("") }
    var showSystem by remember { mutableStateOf(false) }
    var whitelistMode by remember {
        mutableStateOf(AppWhitelistManager.isWhitelistEnabled(context))
    }
    val selected = remember { mutableStateMapOf<String, Boolean>() }

    LaunchedEffect(Unit) {
        scope.launch {
            val list = withContext(Dispatchers.IO) { loadInstalledApps(context) }
            apps = list
            loading = false
            list.forEach { app ->
                selected[app.packageName] =
                    AppWhitelistManager.isPackageSelected(context, app.packageName)
            }
        }
    }

    val filtered = apps.filter { app ->
        (showSystem || !app.isSystem) &&
            (query.isBlank() ||
                app.label.contains(query, ignoreCase = true) ||
                app.packageName.contains(query, ignoreCase = true))
    }

    val selectedCount = selected.count { it.value }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(PageBg)
    ) {
        // 顶部标题栏
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "< 返回",
                fontSize = 15.sp,
                color = BrandOrange,
                fontWeight = FontWeight.Medium,
                modifier = Modifier.clickable(onClick = onBack)
            )
            Text(
                text = "通知应用白名单",
                fontSize = 20.sp,
                fontWeight = FontWeight.Bold,
                color = TextDark,
                modifier = Modifier
                    .weight(1f)
                    .padding(start = 12.dp)
            )
            Text(
                text = "已选 $selectedCount 个",
                fontSize = 13.sp,
                color = BrandOrange,
                fontWeight = FontWeight.Medium
            )
        }

        // 模式设置卡片
        Surface(
            color = androidx.compose.ui.graphics.Color.White,
            shape = RoundedCornerShape(18.dp),
            modifier = Modifier.padding(horizontal = 16.dp)
        ) {
            Column(modifier = Modifier.padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            "白名单模式",
                            fontSize = 15.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = TextDark
                        )
                        Text(
                            "开启后仅同步勾选的应用；关闭时同步所有非系统应用",
                            fontSize = 12.sp,
                            color = TextGray
                        )
                    }
                    Switch(
                        checked = whitelistMode,
                        onCheckedChange = { enabled ->
                            whitelistMode = enabled
                            AppWhitelistManager.setWhitelistEnabled(context, enabled)
                        },
                        colors = SwitchDefaults.colors(
                            checkedThumbColor = androidx.compose.ui.graphics.Color.White,
                            checkedTrackColor = BrandOrange,
                            uncheckedTrackColor = androidx.compose.ui.graphics.Color(0xFFE5E5EA)
                        )
                    )
                }
                Spacer(modifier = Modifier.height(6.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "显示系统应用（短信等系统 App 需开启此项后勾选）",
                        fontSize = 13.sp,
                        color = TextDark,
                        modifier = Modifier.weight(1f)
                    )
                    Switch(
                        checked = showSystem,
                        onCheckedChange = { showSystem = it },
                        colors = SwitchDefaults.colors(
                            checkedThumbColor = androidx.compose.ui.graphics.Color.White,
                            checkedTrackColor = BrandOrange,
                            uncheckedTrackColor = androidx.compose.ui.graphics.Color(0xFFE5E5EA)
                        )
                    )
                }
            }
        }

        // 搜索框
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("搜索应用名称或包名", fontSize = 14.sp) },
            singleLine = true,
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 12.dp)
        )

        if (loading) {
            Box(
                modifier = Modifier.fillMaxSize(),
                contentAlignment = Alignment.Center
            ) {
                CircularProgressIndicator(color = BrandOrange)
            }
        } else {
            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                items(filtered, key = { it.packageName }) { app ->
                    AppRow(
                        app = app,
                        checked = selected[app.packageName] ?: false,
                        onToggle = {
                            val newVal = !(selected[app.packageName] ?: false)
                            selected[app.packageName] = newVal
                            AppWhitelistManager.setPackageSelected(
                                context, app.packageName, newVal
                            )
                        }
                    )
                }
                item { Spacer(modifier = Modifier.height(24.dp)) }
            }
        }
    }
}

@Composable
private fun AppRow(
    app: InstalledApp,
    checked: Boolean,
    onToggle: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(androidx.compose.ui.graphics.Color.White)
            .clickable(onClick = onToggle)
            .padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        val icon = app.icon
        if (icon != null) {
            Image(
                bitmap = icon.asImageBitmap(),
                contentDescription = null,
                modifier = Modifier
                    .size(40.dp)
                    .clip(RoundedCornerShape(10.dp))
            )
        } else {
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(androidx.compose.ui.graphics.Color(0xFFF5F5F7))
            )
        }
        Spacer(modifier = Modifier.width(12.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = app.label,
                fontSize = 15.sp,
                fontWeight = FontWeight.Medium,
                color = TextDark,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = if (app.isSystem) "${app.packageName} · 系统应用" else app.packageName,
                fontSize = 11.sp,
                color = TextGray,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        Checkbox(
            checked = checked,
            onCheckedChange = { onToggle() },
            colors = CheckboxDefaults.colors(
                checkedColor = BrandOrange,
                uncheckedColor = androidx.compose.ui.graphics.Color(0xFFD1D1D6),
                checkmarkColor = androidx.compose.ui.graphics.Color.White
            )
        )
    }
}

private fun loadInstalledApps(context: Context): List<InstalledApp> {
    val pm = context.packageManager
    @Suppress("DEPRECATION")
    val packages = pm.getInstalledApplications(0)
    return packages.map { info ->
        val isSystem = (info.flags and ApplicationInfo.FLAG_SYSTEM) != 0
        val label = pm.getApplicationLabel(info).toString()
        val icon = try {
            drawableToBitmap(pm.getApplicationIcon(info))
        } catch (_: Exception) {
            null
        }
        InstalledApp(
            packageName = info.packageName,
            label = label,
            isSystem = isSystem,
            icon = icon
        )
    }.sortedWith(compareBy({ it.isSystem }, { it.label.lowercase() }))
}

private fun drawableToBitmap(drawable: Drawable): Bitmap {
    val width = if (drawable.intrinsicWidth > 0) drawable.intrinsicWidth else 48
    val height = if (drawable.intrinsicHeight > 0) drawable.intrinsicHeight else 48
    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    val canvas = Canvas(bitmap)
    drawable.setBounds(0, 0, canvas.width, canvas.height)
    drawable.draw(canvas)
    return bitmap
}
