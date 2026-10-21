package com.maifeipin.webmusic.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.maifeipin.webmusic.core.audio.EqualizerManager
import com.maifeipin.webmusic.core.audio.EqualizerState

@Composable
fun EqualizerDialog(
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    var eqState by remember { mutableStateOf(EqualizerManager.getState(context)) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("音频均衡器 (EQ)", fontWeight = FontWeight.Bold, fontSize = 18.sp)
                Switch(
                    checked = eqState?.isEnabled == true,
                    onCheckedChange = { enabled ->
                        EqualizerManager.setEnabled(context, enabled)
                        eqState = EqualizerManager.getState(context)
                    },
                    colors = SwitchDefaults.colors(checkedThumbColor = GreenPrimary)
                )
            }
        },
        text = {
            val state = eqState
            if (state == null) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(180.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text("请先播放任意音频以激活均衡器", color = Color.Gray, fontSize = 14.sp)
                }
            } else {
                Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    // 预设音效横向滑动列表
                    Text("音效预设", color = Color.LightGray, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .horizontalScroll(rememberScrollState()),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        state.presets.forEachIndexed { index, name ->
                            val isSelected = state.currentPreset == index.toShort()
                            FilterChip(
                                selected = isSelected,
                                onClick = {
                                    EqualizerManager.usePreset(context, index.toShort())
                                    eqState = EqualizerManager.getState(context)
                                },
                                label = { Text(name, fontSize = 12.sp) },
                                colors = FilterChipDefaults.filterChipColors(
                                    selectedContainerColor = GreenPrimary.copy(alpha = 0.3f),
                                    selectedLabelColor = GreenPrimary
                                )
                            )
                        }
                    }

                    HorizontalDivider(color = Color.DarkGray, modifier = Modifier.padding(vertical = 4.dp))

                    // 5 频段纵向滑块调节
                    Text("频段增益 (-15dB ~ +15dB)", color = Color.LightGray, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                    state.bands.forEach { band ->
                        val minMb = state.minBandLevelMb.toFloat()
                        val maxMb = state.maxBandLevelMb.toFloat()
                        var currentLevel by remember(band.levelMb) { mutableFloatStateOf(band.levelMb.toFloat()) }

                        val freqLabel = if (band.centerFreqHz >= 1000) {
                            "${"%.1f".format(band.centerFreqHz / 1000f)} kHz"
                        } else {
                            "${band.centerFreqHz} Hz"
                        }
                        val gainDb = currentLevel / 100f // 100 mB = 1 dB

                        Column(modifier = Modifier.fillMaxWidth()) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                Text(freqLabel, color = Color.White, fontSize = 13.sp)
                                Text(
                                    text = "${if (gainDb > 0) "+" else ""}${"%.1f".format(gainDb)} dB",
                                    color = if (gainDb > 0) GreenPrimary else if (gainDb < 0) Color(0xFFFF5252) else Color.Gray,
                                    fontSize = 12.sp,
                                    fontWeight = FontWeight.Medium
                                )
                            }
                            Slider(
                                value = currentLevel,
                                onValueChange = { newVal ->
                                    currentLevel = newVal
                                },
                                onValueChangeFinished = {
                                    EqualizerManager.setBandLevel(context, band.bandIndex, currentLevel.toInt().toShort())
                                    eqState = EqualizerManager.getState(context)
                                },
                                valueRange = minMb..maxMb,
                                enabled = state.isEnabled,
                                colors = SliderDefaults.colors(
                                    thumbColor = GreenPrimary,
                                    activeTrackColor = GreenPrimary,
                                    inactiveTrackColor = Color.DarkGray
                                )
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            Button(
                onClick = onDismiss,
                colors = ButtonDefaults.buttonColors(containerColor = GreenPrimary)
            ) {
                Text("完成", color = Color.Black, fontWeight = FontWeight.Bold)
            }
        },
        containerColor = Color(0xFF1E2620),
        shape = RoundedCornerShape(16.dp)
    )
}
