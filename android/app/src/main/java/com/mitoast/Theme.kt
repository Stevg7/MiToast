package com.mitoast

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// ---------- HyperOS 设计令牌（取自 miuix 组件库 theme/Colors.kt） ----------

/**
 * HyperOS 色板。浅色：页面底 #F7F7F7、白卡片浮在上面、主色 HyperOS 蓝 #3482FF；
 * 深色：页面底纯黑、卡片 #242424、主色 #277AF7（miuix 暗色主色与浅色不同值，别混用）。
 */
data class HyperosColors(
    val primary: Color,
    val pageBg: Color,
    val card: Color,
    val text: Color,
    val textSecondary: Color,
    val sectionTitle: Color,
    val track: Color,
    val field: Color,
    val divider: Color,
    val isDark: Boolean
)

private val LightColors = HyperosColors(
    primary = Color(0xFF3482FF),
    pageBg = Color(0xFFF7F7F7),
    card = Color.White,
    text = Color(0xFF000000),
    textSecondary = Color(0x99000000),
    sectionTitle = Color(0xFF8C93B0),
    track = Color(0xFFE6E6E6),
    field = Color(0xFFF0F0F0),
    divider = Color(0xFFE0E0E0),
    isDark = false
)

private val DarkColors = HyperosColors(
    primary = Color(0xFF277AF7),
    pageBg = Color(0xFF000000),
    card = Color(0xFF242424),
    text = Color(0xFFF2F2F2),
    textSecondary = Color(0x80FFFFFF),
    sectionTitle = Color(0xFF787E96),
    track = Color(0xFF505050),
    field = Color(0xFF434343),
    divider = Color(0xFF393939),
    isDark = true
)

val LocalHyperos = staticCompositionLocalOf { LightColors }

/** 取当前 HyperOS 色板（随系统深浅色）。 */
@Composable
fun hyperos(): HyperosColors = LocalHyperos.current

/** 状态点绿（沿用原成功色；只作状态语义，不随 HyperOS 主色变化）。 */
val SuccessGreen = Color(0xFF34C759)

@Composable
fun MiToastTheme(content: @Composable () -> Unit) {
    val colors = if (isSystemInDarkTheme()) DarkColors else LightColors
    val scheme = if (colors.isDark) darkColorScheme(
        primary = colors.primary,
        onPrimary = Color.White,
        background = colors.pageBg,
        onBackground = colors.text,
        surface = colors.card,
        onSurface = colors.text,
        surfaceVariant = colors.field,
        onSurfaceVariant = colors.textSecondary,
        outline = colors.divider
    ) else lightColorScheme(
        primary = colors.primary,
        onPrimary = Color.White,
        background = colors.pageBg,
        onBackground = colors.text,
        surface = colors.card,
        onSurface = colors.text,
        surfaceVariant = colors.field,
        onSurfaceVariant = colors.textSecondary,
        outline = colors.divider
    )
    CompositionLocalProvider(LocalHyperos provides colors) {
        MaterialTheme(colorScheme = scheme, content = content)
    }
}

// ---------- HyperOS 组件（规格对齐 miuix） ----------

/**
 * HyperOS 开关（miuix Switch 规格）：轨道 49×28 全圆角胶囊、thumb 20 恒白、
 * 关态 thumb 距边 4dp、开态 25dp；选中轨道用主色（miuix 无独立 checked 令牌）。
 */
@Composable
fun HyperSwitch(checked: Boolean, onCheckedChange: (Boolean) -> Unit, modifier: Modifier = Modifier) {
    val c = hyperos()
    val trackColor by animateColorAsState(if (checked) c.primary else c.track, label = "switchTrack")
    val thumbStart by animateDpAsState(if (checked) 25.dp else 4.dp, label = "switchThumb")
    Box(
        modifier = modifier
            .size(width = 49.dp, height = 28.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(trackColor)
            .clickable { onCheckedChange(!checked) },
        contentAlignment = Alignment.CenterStart
    ) {
        Box(
            modifier = Modifier
                .padding(start = thumbStart)
                .size(20.dp)
                .clip(CircleShape)
                .background(Color.White)
        )
    }
}

/** HyperOS 复选（miuix Checkbox 规格）：26dp 正圆，选中填充主色、白色对勾、无描边框。 */
@Composable
fun HyperCheckbox(checked: Boolean, onCheckedChange: () -> Unit, modifier: Modifier = Modifier) {
    val c = hyperos()
    Box(
        modifier = modifier
            .size(26.dp)
            .clip(CircleShape)
            .background(if (checked) c.primary else c.track)
            .clickable(onClick = onCheckedChange),
        contentAlignment = Alignment.Center
    ) {
        if (checked) {
            Text("✓", color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.Bold)
        }
    }
}
