import { useEffect, useRef, useState } from 'react';
import { usePlayer } from '../context/PlayerContext';
import { useAuth } from '../context/AuthContext';
import SmbImage from './SmbImage';
import { Play, Pause, Music, SkipBack, SkipForward, AlertTriangle, Minimize2, Maximize2, Heart, ListPlus, Edit2, ListOrdered, Mic2, ChevronDown } from 'lucide-react';
import { LyricsPanel } from './LyricsPanel';
import AddToPlaylistModal from './AddToPlaylistModal';
import EditSongModal from './EditSongModal';

export default function GlobalPlayer() {
    const {
        currentSong, isPlaying, togglePlay, showTranscodePrompt, confirmTranscode, transcodeMode, nextSong, prevSong,
        isFavorite, toggleLike, updateCurrentSong, restoredTime, saveProgress, queue, playQueue, currentIndex
    } = usePlayer();
    const { token } = useAuth();
    const audioRef = useRef<HTMLAudioElement>(null);

    // Desktop State
    const [isMinimized, setIsMinimized] = useState(false);
    // Mobile State
    const [mobileExpand, setMobileExpand] = useState(false);

    const [currentTime, setCurrentTime] = useState(0);
    const [duration, setDuration] = useState(0);
    const [seekOffset, setSeekOffset] = useState(0);
    const [showPlaylistModal, setShowPlaylistModal] = useState(false);
    const [showEditModal, setShowEditModal] = useState(false);
    const [showQueue, setShowQueue] = useState(false);
    const [showLyrics, setShowLyrics] = useState(false);

    // Resume Time
    useEffect(() => {
        if (restoredTime > 0 && audioRef.current && currentSong && !transcodeMode) {
            audioRef.current.currentTime = restoredTime;
            setCurrentTime(restoredTime);
        }
    }, [restoredTime, currentSong, transcodeMode]);

    // Reset state on song change
    useEffect(() => {
        setSeekOffset(0);
        if (restoredTime === 0) setCurrentTime(0);
        setDuration(currentSong?.duration || 0);
    }, [currentSong?.id, currentSong?.duration]);

    // Save Progress Interval
    useEffect(() => {
        if (!isPlaying || !currentSong) return;
        const interval = setInterval(() => {
            saveProgress(audioRef.current?.currentTime || 0);
        }, 5000);
        return () => {
            clearInterval(interval);
            saveProgress(audioRef.current?.currentTime || 0);
        };
    }, [isPlaying, currentSong?.id]);

    // Media Session API
    useEffect(() => {
        if (!currentSong || !('mediaSession' in navigator)) return;

        navigator.mediaSession.metadata = new MediaMetadata({
            title: currentSong.title || 'Unknown',
            artist: currentSong.artist || 'Unknown Artist',
            album: currentSong.album || 'Unknown Album',
            artwork: [
                { src: '/icon-192.svg', sizes: '192x192', type: 'image/svg+xml' },
                { src: '/icon-512.svg', sizes: '512x512', type: 'image/svg+xml' },
            ]
        });

        navigator.mediaSession.setActionHandler('play', () => { if (!isPlaying) togglePlay(); });
        navigator.mediaSession.setActionHandler('pause', () => { if (isPlaying) togglePlay(); });
        navigator.mediaSession.setActionHandler('previoustrack', prevSong);
        navigator.mediaSession.setActionHandler('nexttrack', nextSong);
        navigator.mediaSession.setActionHandler('seekbackward', () => {
            if (audioRef.current) audioRef.current.currentTime = Math.max(0, audioRef.current.currentTime - 10);
        });
        navigator.mediaSession.setActionHandler('seekforward', () => {
            if (audioRef.current) audioRef.current.currentTime = Math.min(audioRef.current.duration || 0, audioRef.current.currentTime + 10);
        });
    }, [currentSong, isPlaying, togglePlay, prevSong, nextSong]);

    useEffect(() => {
        if ('mediaSession' in navigator) {
            navigator.mediaSession.playbackState = isPlaying ? 'playing' : 'paused';
        }
    }, [isPlaying]);

    useEffect(() => {
        if ('mediaSession' in navigator && 'setPositionState' in navigator.mediaSession) {
            const totalDur = currentSong?.duration || duration || 0;
            if (totalDur > 0) {
                try {
                    navigator.mediaSession.setPositionState({
                        duration: totalDur,
                        playbackRate: 1,
                        position: Math.min(currentTime, totalDur)
                    });
                } catch (e) { }
            }
        }
    }, [currentTime, duration, currentSong?.duration]);

    // Draggable State (Desktop Only)
    const [position, setPosition] = useState({ x: 0, y: 0 });
    const isDragging = useRef(false);
    const dragStart = useRef({ x: 0, y: 0 });

    useEffect(() => {
        if (audioRef.current && currentSong) {
            if (isPlaying) {
                audioRef.current.play().catch(e => {
                    if (e.name !== 'NotSupportedError' && e.name !== 'AbortError') console.error("Play error:", e);
                });
            } else {
                audioRef.current.pause();
            }
        }
    }, [isPlaying, currentSong]);

    const handleMouseMove = (e: MouseEvent) => {
        if (!isDragging.current) return;
        setPosition({ x: e.clientX - dragStart.current.x, y: e.clientY - dragStart.current.y });
    };

    const handleMouseUp = () => {
        isDragging.current = false;
        document.removeEventListener('mousemove', handleMouseMove);
        document.removeEventListener('mouseup', handleMouseUp);
    };

    const handleMouseDown = (e: React.MouseEvent) => {
        if ((e.target as HTMLElement).closest('input[type="range"]')) return;
        isDragging.current = true;
        dragStart.current = { x: e.clientX - position.x, y: e.clientY - position.y };
        document.addEventListener('mousemove', handleMouseMove);
        document.addEventListener('mouseup', handleMouseUp);
    };

    const handleSeek = (e: React.ChangeEvent<HTMLInputElement>) => {
        const time = parseFloat(e.target.value);
        if (transcodeMode) {
            setSeekOffset(time);
            setCurrentTime(0);
        } else {
            if (audioRef.current) audioRef.current.currentTime = time;
            setCurrentTime(time);
        }
    };

    const formatTime = (time: number) => {
        if (!isFinite(time)) return "0:00";
        const minutes = Math.floor(time / 60);
        const seconds = Math.floor(time % 60);
        return `${minutes}:${seconds.toString().padStart(2, '0')}`;
    };

    const displayTime = transcodeMode ? (seekOffset + currentTime) : currentTime;
    const totalDuration = currentSong?.duration || duration || 0;

    const streamUrl = (currentSong && token)
        ? `/api/media/stream/${currentSong.id}?access_token=${token}${transcodeMode ? `&transcode=true&startTime=${seekOffset}` : ''}`
        : undefined;

    return (
        <>
            {currentSong && (
                <>
                    {/* ================= DESKTOP LAYOUT (Hidden on Mobile) ================= */}
                    <div
                        className={`hidden md:block fixed z-50 transition-all duration-300 ${isMinimized ? 'w-64' : 'w-96 md:w-[32rem]'}`}
                        style={{
                            bottom: '2rem',
                            right: '2rem',
                            transform: `translate(${position.x}px, ${position.y}px)`,
                            cursor: isDragging.current ? 'grabbing' : 'default'
                        }}
                    >
                        <div className="bg-black/60 backdrop-blur-xl border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
                            {/* Header / Handle */}
                            <div
                                onMouseDown={handleMouseDown}
                                className="h-6 bg-white/5 cursor-grab active:cursor-grabbing flex items-center justify-center hover:bg-white/10 transition"
                            >
                                <div className="w-12 h-1 bg-white/20 rounded-full mt-2" />
                            </div>

                            {/* Content */}
                            <div className="p-4 pt-2">
                                <div className="flex gap-4">
                                    <div className={`relative bg-gray-800 rounded-lg overflow-hidden flex-shrink-0 transition-all ${isMinimized ? 'w-10 h-10' : 'w-16 h-16'}`}>
                                        {currentSong.coverArt ? (
                                            <SmbImage
                                                smbPath={currentSong.coverArt}
                                                className="w-full h-full object-cover"
                                                alt={currentSong.album || 'Cover'}
                                            />
                                        ) : (
                                            <div className="absolute inset-0 flex items-center justify-center bg-gradient-to-br from-blue-900 to-purple-900">
                                                <Music size={isMinimized ? 16 : 24} className="text-white/50" />
                                            </div>
                                        )}
                                    </div>

                                    <div className="flex-1 min-w-0 flex flex-col justify-center">
                                        <div className="flex items-start justify-between">
                                            <div className="overflow-hidden flex-1 mr-4">
                                                <div className="flex items-center gap-3">
                                                    <h4 className="font-bold text-white truncate leading-tight select-none text-lg">
                                                        {currentSong.title}
                                                    </h4>
                                                    <button onClick={() => toggleLike(currentSong.id)} className="hover:scale-110 transition active:scale-95 flex-shrink-0" onMouseDown={e => e.stopPropagation()}>
                                                        <Heart size={20} className={isFavorite(currentSong.id) ? "text-red-500" : "text-gray-400 hover:text-white"} fill={isFavorite(currentSong.id) ? "currentColor" : "none"} />
                                                    </button>
                                                    <button onClick={() => setShowPlaylistModal(true)} className="hover:scale-110 transition active:scale-95 flex-shrink-0 text-gray-400 hover:text-white" onMouseDown={e => e.stopPropagation()} title="Add to Playlist">
                                                        <ListPlus size={20} />
                                                    </button>
                                                    <button onClick={() => setShowQueue(!showQueue)} className={`hover:scale-110 transition active:scale-95 flex-shrink-0 ${showQueue ? 'text-blue-400' : 'text-gray-400 hover:text-white'}`} onMouseDown={e => e.stopPropagation()} title="Current Queue">
                                                        <ListOrdered size={20} />
                                                    </button>
                                                    <button onClick={() => setShowEditModal(true)} className="hover:scale-110 transition active:scale-95 flex-shrink-0 text-gray-400 hover:text-blue-400" onMouseDown={e => e.stopPropagation()} title="Edit Song Info">
                                                        <Edit2 size={18} />
                                                    </button>
                                                    <button onClick={() => setShowLyrics(!showLyrics)} className={`hover:scale-110 transition active:scale-95 flex-shrink-0 ${showLyrics ? 'text-purple-400' : 'text-gray-400 hover:text-purple-400'}`} onMouseDown={e => e.stopPropagation()} title="Lyrics">
                                                        <Mic2 size={18} />
                                                    </button>
                                                </div>
                                                <p className="text-gray-400 text-sm truncate mt-0.5 select-none">{currentSong.artist}</p>
                                            </div>
                                            <button onClick={() => setIsMinimized(!isMinimized)} className="text-gray-500 hover:text-white p-1 ml-2" onMouseDown={e => e.stopPropagation()}>
                                                {isMinimized ? <Maximize2 size={14} /> : <Minimize2 size={14} />}
                                            </button>
                                        </div>
                                        {transcodeMode && !isMinimized && (
                                            <span className="text-[10px] text-yellow-500 border border-yellow-500/30 bg-yellow-500/10 px-1.5 py-0.5 rounded w-fit mt-1 select-none">
                                                Transcoding {seekOffset > 0 && `(+${Math.round(seekOffset)}s)`}
                                            </span>
                                        )}
                                    </div>
                                </div>

                                {showQueue && !isMinimized && (
                                    <div className="mt-4 bg-black/40 rounded-lg p-2 max-h-48 overflow-y-auto custom-scrollbar border border-white/5" onMouseDown={e => e.stopPropagation()}>
                                        <div className="text-xs text-gray-400 mb-2 px-1 flex justify-between">
                                            <span>Queue ({queue.length})</span>
                                            <button onClick={() => setShowQueue(false)}>Close</button>
                                        </div>
                                        <div className="space-y-1">
                                            {queue.map((song, idx) => (
                                                <div key={`${song.id}-${idx}`} onClick={() => playQueue(queue, idx)} className={`text-sm py-1.5 px-2 rounded cursor-pointer truncate flex items-center justify-between group ${idx === currentIndex ? 'bg-blue-600/30 text-blue-200 font-medium' : 'hover:bg-white/5 text-gray-300'}`}>
                                                    <span className="truncate flex-1">{idx + 1}. {song.title}</span>
                                                    <span className="text-xs text-gray-500 ml-2 group-hover:block hidden">{song.artist}</span>
                                                </div>
                                            ))}
                                        </div>
                                    </div>
                                )}

                                {!isMinimized && !showQueue && (
                                    <div className="mt-4 flex items-center justify-center gap-6">
                                        <button onClick={prevSong} className="text-gray-400 hover:text-white transition hover:scale-110" onMouseDown={e => e.stopPropagation()}><SkipBack size={20} /></button>
                                        <button onClick={togglePlay} className="bg-white text-black rounded-full p-3 hover:scale-105 transition shadow-lg shadow-white/10" onMouseDown={e => e.stopPropagation()}>
                                            {isPlaying ? <Pause fill="currentColor" size={24} /> : <Play fill="currentColor" size={24} />}
                                        </button>
                                        <button onClick={nextSong} className="text-gray-400 hover:text-white transition hover:scale-110" onMouseDown={e => e.stopPropagation()}><SkipForward size={20} /></button>
                                    </div>
                                )}

                                {!isMinimized && !showQueue && (
                                    <div className="mt-4 space-y-1">
                                        <input type="range" min={0} max={totalDuration} value={displayTime} onChange={handleSeek} className="w-full h-1 bg-white/10 rounded-lg appearance-none cursor-pointer accent-blue-500 hover:accent-blue-400" onMouseDown={e => e.stopPropagation()} />
                                        <div className="flex justify-between text-[10px] text-gray-500 font-mono">
                                            <span>{formatTime(displayTime)}</span>
                                            <span>{formatTime(totalDuration)}</span>
                                        </div>
                                    </div>
                                )}
                            </div>
                        </div>
                    </div>

                    {/* Mobile Mini Player */}
                    <div className={`md:hidden fixed z-[50] w-full transition-all duration-300 ${mobileExpand ? 'bottom-0 translate-y-full opacity-0 pointer-events-none' : 'bottom-[70px] opacity-100'}`} style={{ paddingBottom: 'env(safe-area-inset-bottom)' }}>
                        <div onClick={() => setMobileExpand(true)} className="mx-4 h-14 bg-gray-900/80 backdrop-blur-xl border border-white/5 rounded-full flex items-center px-2 pr-4 shadow-lg active:scale-95 transition-all cursor-pointer">
                            {/* Spin Record Art */}
                            <div className={`w-10 h-10 rounded-full flex items-center justify-center border border-white/10 shrink-0 relative overflow-hidden ${isPlaying ? 'animate-spin-slow' : ''}`}>
                                {currentSong.coverArt ? (
                                    <SmbImage
                                        smbPath={currentSong.coverArt}
                                        className="w-full h-full object-cover"
                                        fallbackClassName="text-gray-400 w-5 h-5"
                                    />
                                ) : (
                                    <div className="w-full h-full bg-gray-800 flex items-center justify-center">
                                        <Music size={16} className="text-gray-400" />
                                    </div>
                                )}
                            </div>

                            <div className="flex-1 min-w-0 mx-3 flex flex-col justify-center">
                                <div className="text-white font-medium truncate text-sm">{currentSong.title}</div>
                                <div className="text-gray-400 text-xs truncate">{currentSong.artist}</div>
                            </div>

                            {/* Controls (Mini) */}
                            <button
                                onClick={(e) => { e.stopPropagation(); toggleLike(currentSong.id); }}
                                className="p-2 text-gray-400"
                            >
                                <Heart size={18} className={isFavorite(currentSong.id) ? "text-red-500" : ""} fill={isFavorite(currentSong.id) ? "currentColor" : "none"} />
                            </button>
                            <button
                                onClick={(e) => { e.stopPropagation(); togglePlay(); }}
                                className="p-2 text-white"
                            >
                                {isPlaying ? <Pause size={20} fill="currentColor" /> : <Play size={20} fill="currentColor" />}
                            </button>
                        </div>
                    </div>


                    {/* ================= MOBILE FULL SCREEEN (Overlay) ================= */}
                    {mobileExpand && (
                        <div className="md:hidden fixed inset-0 z-[60] bg-gray-950 flex flex-col p-6 pb-12 animate-in slide-in-from-bottom duration-300">
                            {/* Header */}
                            <div className="flex items-center justify-between mb-8">
                                <button onClick={() => setMobileExpand(false)} className="text-gray-400 p-2">
                                    <ChevronDown size={28} />
                                </button>
                                <span className="text-xs uppercase tracking-widest text-gray-500">Now Playing</span>
                                <button onClick={() => setShowQueue(!showQueue)} className={`p-2 ${showQueue ? 'text-blue-500' : 'text-gray-400'}`}>
                                    <ListOrdered size={24} />
                                </button>
                            </div>

                            {/* Queue View in Full Screen */}
                            {showQueue ? (
                                <div className="flex-1 overflow-y-auto custom-scrollbar mb-8 bg-gray-900/50 rounded-2xl p-4">
                                    <div className="space-y-4">
                                        {queue.map((song, idx) => (
                                            <div
                                                key={`${song.id}-${idx}-mb`}
                                                onClick={() => playQueue(queue, idx)}
                                                className={`flex items-center gap-3 p-2 rounded-lg ${idx === currentIndex ? 'bg-blue-500/20 text-blue-400' : 'text-gray-400'}`}
                                            >
                                                <span className="text-sm font-mono w-6 opacity-50">{idx + 1}</span>
                                                <div className="flex-1 min-w-0">
                                                    <div className="font-medium truncate text-base">{song.title}</div>
                                                    <div className="text-sm opacity-60 truncate">{song.artist}</div>
                                                </div>
                                                {idx === currentIndex && <div className="w-2 h-2 rounded-full bg-blue-500" />}
                                            </div>
                                        ))}
                                    </div>
                                </div>
                            ) : (
                                /* Normal Full Screen View */
                                <>
                                    {/* Big Cover */}
                                    <div className="aspect-square w-full bg-gradient-to-br from-blue-900 to-purple-900 rounded-2xl mb-8 flex items-center justify-center shadow-2xl shadow-blue-900/10 relative group overflow-hidden">
                                        {currentSong.coverArt ? (
                                            <SmbImage
                                                smbPath={currentSong.coverArt}
                                                className="w-full h-full object-cover"
                                                fallbackClassName="text-white/20 w-32 h-32"
                                            />
                                        ) : (
                                            <Music size={80} className="text-white/20" />
                                        )}

                                        {/* Edit Button (Mobile) */}
                                        <button
                                            onClick={(e) => {
                                                e.stopPropagation();
                                                setShowEditModal(true);
                                            }}
                                            className="absolute bottom-3 right-3 p-3 bg-black/40 backdrop-blur-md rounded-full text-white/70 hover:text-white border border-white/10 active:scale-90 transition shadow-lg"
                                        >
                                            <Edit2 size={18} />
                                        </button>
                                    </div>

                                    {/* Info Block */}
                                    <div className="flex items-start justify-between mb-8">
                                        <div className="flex-1 min-w-0 pr-4">
                                            <h2 className="text-2xl font-bold text-white truncate mb-1">{currentSong.title}</h2>
                                            <p className="text-lg text-gray-400 truncate">{currentSong.artist}</p>
                                        </div>
                                        <button onClick={() => toggleLike(currentSong.id)} className="p-2">
                                            <Heart size={28} className={isFavorite(currentSong.id) ? "text-red-500" : "text-gray-500"} fill={isFavorite(currentSong.id) ? "currentColor" : "none"} />
                                        </button>
                                    </div>

                                    {/* Progress */}
                                    <div className="mb-8">
                                        <input
                                            type="range"
                                            min={0}
                                            max={totalDuration}
                                            value={displayTime}
                                            onChange={handleSeek}
                                            className="w-full h-1.5 bg-gray-800 rounded-full appearance-none cursor-pointer accent-white"
                                        />
                                        <div className="flex justify-between text-xs text-gray-500 font-medium mt-2">
                                            <span>{formatTime(displayTime)}</span>
                                            <span>{formatTime(totalDuration)}</span>
                                        </div>
                                    </div>

                                    {/* Controls */}
                                    <div className="flex items-center justify-between px-4 mb-8">
                                        <button className="text-gray-400 hover:text-white" onClick={() => setShowLyrics(!showLyrics)}>
                                            <Mic2 size={24} className={showLyrics ? "text-purple-500" : ""} />
                                        </button>
                                        <div className="flex items-center gap-6">
                                            <button onClick={prevSong} className="text-white p-2 active:scale-90 transition"><SkipBack size={32} /></button>
                                            <button onClick={togglePlay} className="bg-white text-black rounded-full p-5 active:scale-95 transition shadow-xl">
                                                {isPlaying ? <Pause fill="currentColor" size={32} /> : <Play fill="currentColor" size={32} />}
                                            </button>
                                            <button onClick={nextSong} className="text-white p-2 active:scale-90 transition"><SkipForward size={32} /></button>
                                        </div>
                                        <button className="text-gray-400 hover:text-white" onClick={() => setShowPlaylistModal(true)}>
                                            <ListPlus size={24} />
                                        </button>
                                    </div>
                                </>
                            )}
                        </div>
                    )}
                </>
            )}

            <audio
                ref={audioRef}
                src={streamUrl}
                autoPlay={isPlaying}
                onPlay={() => { /* Sync state if needed */ }}
                onPause={() => { /* Sync state if needed */ }}
                onEnded={nextSong}
                onError={(e) => console.error("Audio Load Error", e)}
                onTimeUpdate={(e) => setCurrentTime(e.currentTarget.currentTime)}
                onLoadedMetadata={(e) => {
                    const audio = e.currentTarget;
                    if (!transcodeMode && audio.duration && isFinite(audio.duration)) {
                        setDuration(audio.duration);
                    }
                }}
            />

            {showTranscodePrompt && (
                <div className="fixed inset-0 bg-black/80 z-[100] flex items-center justify-center p-4 backdrop-blur-sm">
                    <div className="bg-gray-900 p-6 rounded-2xl max-w-md w-full border border-gray-800 shadow-2xl">
                        <div className="flex items-center gap-3 text-yellow-500 mb-4">
                            <AlertTriangle size={32} />
                            <h2 className="text-xl font-bold text-white">Unsupported Format</h2>
                        </div>
                        <p className="text-gray-300 mb-6 leading-relaxed">
                            This format may not work in your browser. <br />
                            Transcode to MP3 for playback?
                        </p>
                        <div className="flex justify-end gap-3">
                            <button onClick={() => confirmTranscode(false)} className="px-4 py-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/5 transition">Cancel</button>
                            <button onClick={() => confirmTranscode(true)} className="px-4 py-2 rounded-lg bg-blue-600 hover:bg-blue-500 text-white font-medium shadow-lg shadow-blue-500/20 transition">Transcode & Play</button>
                        </div>
                    </div>
                </div>
            )}

            <AddToPlaylistModal isOpen={showPlaylistModal} onClose={() => setShowPlaylistModal(false)} songIds={currentSong?.id ? [currentSong.id] : []} />
            <EditSongModal isOpen={showEditModal} onClose={() => setShowEditModal(false)} song={currentSong} onSaved={updateCurrentSong} />

            {showLyrics && currentSong && (
                <LyricsPanel mediaId={currentSong.id} currentTime={displayTime} onClose={() => setShowLyrics(false)} />
            )}
        </>
    );
}
