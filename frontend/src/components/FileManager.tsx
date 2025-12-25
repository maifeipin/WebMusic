import React, { useState, useEffect, useRef } from 'react';
import axios from 'axios';
import { browseFiles, createDirectory, uploadFile, deleteFile, type FileItem } from '../services/files';
import { DuplicateCleanerModal } from './DuplicateCleanerModal';
import { X, Folder, FileText, HardDrive, ArrowLeft, Upload, Plus, Trash2, Download, Share2, AlertTriangle, Music, Video, Image as ImageIcon, FileCode } from 'lucide-react';

interface FileManagerProps {
    onClose: () => void;
}

interface FileSystemEntry {
    isFile: boolean;
    isDirectory: boolean;
    name: string;
    fullPath: string;
}
interface FileSystemFileEntry extends FileSystemEntry {
    file: (success: (file: File) => void, error?: (err: DOMException) => void) => void;
}
interface FileSystemDirectoryEntry extends FileSystemEntry {
    createReader: () => FileSystemDirectoryReader;
}
interface FileSystemDirectoryReader {
    readEntries: (success: (entries: FileSystemEntry[]) => void, error?: (err: DOMException) => void) => void;
}
interface UploadTask {
    file: File;
    relativePath: string;
}

export const FileManager: React.FC<FileManagerProps> = ({ onClose }) => {
    const [currentPath, setCurrentPath] = useState('');
    const [currentSourceId, setCurrentSourceId] = useState<number | undefined>(undefined);
    const [items, setItems] = useState<FileItem[]>([]);
    const [loading, setLoading] = useState(false);
    // Removed simple uploadProgress state, replaced by uploadStatus
    const [showNewFolderInput, setShowNewFolderInput] = useState(false);
    const [newFolderName, setNewFolderName] = useState('');

    const [currentBasePath, setCurrentBasePath] = useState('');

    // History Stack for Back Nav: { sid, path, basePath }
    const [history, setHistory] = useState<{ sid: number | undefined, path: string, basePath: string }[]>([]);

    const [uploadStatus, setUploadStatus] = useState<{
        active: boolean;
        currentIndex: number;
        total: number;
        currentFile: string;
        progress: number;
        failures: string[];
    }>({ active: false, currentIndex: 0, total: 0, currentFile: '', progress: 0, failures: [] });

    const abortControllerRef = useRef<AbortController | null>(null);
    const loadRef = useRef(false);

    const loadFiles = async (sid?: number, path: string = '') => {
        setLoading(true);
        try {
            const data = await browseFiles(sid, path);
            setItems(data);
            setCurrentSourceId(sid);
            setCurrentPath(path);
        } catch (err) {
            console.error(err);
            alert("Failed to load files");
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        if (!loadRef.current) {
            loadFiles(undefined, '');
            loadRef.current = true;
        }
    }, []);

    const handleNavigate = (item: FileItem) => {
        if (item.type === 'File') return;

        // Push current into history
        setHistory([...history, { sid: currentSourceId, path: currentPath, basePath: currentBasePath }]);

        if (item.type === 'Source') {
            setCurrentBasePath(item.path || ''); // Capture Source Path
            loadFiles(item.sourceId, '');
        } else {
            // Directory
            loadFiles(currentSourceId, item.path);
        }
    };

    const handleBack = () => {
        if (history.length === 0) return;
        const previous = history[history.length - 1];
        setHistory(history.slice(0, -1));
        setCurrentBasePath(previous.basePath);
        loadFiles(previous.sid, previous.path);
    };

    const [isDragging, setIsDragging] = useState(false);

    const processUpload = async (tasks: UploadTask[]) => {
        if (!tasks.length || !currentSourceId) return;

        // Collect Directories
        const directories = new Set<string>();
        tasks.forEach(t => {
            if (!t.relativePath) return;
            const parts = t.relativePath.split('/');
            let accum = "";
            parts.forEach(p => {
                accum = accum ? `${accum}/${p}` : p;
                directories.add(accum);
            });
        });
        const dirList = Array.from(directories).sort((a, b) => a.length - b.length);

        setUploadStatus({ active: true, currentIndex: 0, total: tasks.length + dirList.length, currentFile: 'Preparing...', progress: 0, failures: [] });

        abortControllerRef.current = new AbortController();
        const signal = abortControllerRef.current.signal;

        // Mkdir Phase
        for (const dir of dirList) {
            if (signal.aborted) break;
            setUploadStatus(prev => ({ ...prev, currentFile: `Creating folder: ${dir}`, currentIndex: prev.currentIndex + 1 }));
            try {
                const fullPath = currentPath ? `${currentPath}/${dir}` : dir;
                await createDirectory(currentSourceId, fullPath);
            } catch (e) {
                // Ignore exists error
            }
        }

        let failures: string[] = [];

        // File Upload Phase
        for (let i = 0; i < tasks.length; i++) {
            if (signal.aborted) break;

            const task = tasks[i];
            const targetDir = currentPath ? (task.relativePath ? `${currentPath}/${task.relativePath}` : currentPath) : task.relativePath;

            setUploadStatus(prev => ({ ...prev, currentIndex: prev.currentIndex + 1, currentFile: task.file.name, progress: 0 }));

            try {
                await uploadFile(currentSourceId, targetDir, task.file, (pct) => {
                    setUploadStatus(prev => ({ ...prev, progress: pct }));
                }, signal);
            } catch (err: any) {
                if (axios.isCancel(err)) break;
                console.error(`Failed to upload ${task.file.name}`, err);
                failures.push(task.file.name);
                setUploadStatus(prev => ({ ...prev, failures: [...prev.failures, task.file.name] }));
            }
        }

        loadFiles(currentSourceId, currentPath);

        if (!signal.aborted) {
            if (failures.length > 0) alert(`Upload Finished with ${failures.length} errors:\n${failures.join('\n')}`);
            if (failures.length === 0) setTimeout(() => setUploadStatus(prev => ({ ...prev, active: false })), 1000);
            else setUploadStatus(prev => ({ ...prev, active: false }));
        } else {
            setUploadStatus(prev => ({ ...prev, active: false }));
        }
    };

    const handleUpload = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files?.length) {
            const tasks: UploadTask[] = Array.from(e.target.files).map(f => ({ file: f, relativePath: '' }));
            processUpload(tasks);
        }
        e.target.value = '';
    };

    const handleDragEnter = (e: React.DragEvent) => {
        e.preventDefault();
        e.stopPropagation();
        if (e.dataTransfer.types.includes('Files')) setIsDragging(true);
    };

    const handleOverlayDragLeave = (e: React.DragEvent) => {
        e.preventDefault();
        e.stopPropagation();
        setIsDragging(false);
    };

    const scanEntries = async (items: DataTransferItemList): Promise<UploadTask[]> => {
        const tasks: UploadTask[] = [];
        const scan = async (entry: FileSystemEntry, path: string) => {
            if (entry.isFile) {
                const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject as any));
                tasks.push({ file, relativePath: path });
            } else if (entry.isDirectory) {
                const dirReader = (entry as FileSystemDirectoryEntry).createReader();
                const readAll = async () => {
                    let all: FileSystemEntry[] = [];
                    let batch = await new Promise<FileSystemEntry[]>((r, j) => dirReader.readEntries(r, j as any));
                    while (batch.length > 0) {
                        all = all.concat(batch);
                        batch = await new Promise<FileSystemEntry[]>((r, j) => dirReader.readEntries(r, j as any));
                    }
                    return all;
                };
                const entries = await readAll();
                const dirPath = path ? `${path}/${entry.name}` : entry.name;
                for (const child of entries) await scan(child, dirPath);
            }
        };

        for (let i = 0; i < items.length; i++) {
            const entry = (items[i] as any).webkitGetAsEntry();
            if (entry) await scan(entry, "");
        }
        return tasks;
    };

    const handleOverlayDrop = async (e: React.DragEvent) => {
        e.preventDefault();
        e.stopPropagation();
        setIsDragging(false);
        const tasks = await scanEntries(e.dataTransfer.items);
        if (tasks.length > 0) processUpload(tasks);
    };

    const cancelUpload = () => {
        if (abortControllerRef.current) {
            abortControllerRef.current.abort();
            setUploadStatus(prev => ({ ...prev, active: false }));
        }
    };

    const handleCreateFolder = async () => {
        if (!newFolderName || !currentSourceId) return;

        try {
            const newPath = currentPath ? `${currentPath}/${newFolderName}` : newFolderName;
            await createDirectory(currentSourceId, newPath);
            setNewFolderName('');
            setShowNewFolderInput(false);
            loadFiles(currentSourceId, currentPath);
        } catch (err) {
            console.error(err);
            alert("Failed to create folder");
        }
    };

    const handleDelete = async (e: React.MouseEvent, item: FileItem) => {
        e.stopPropagation(); // Prevent navigation
        if (!currentSourceId) return;

        const confirmMsg = `Are you sure you want to delete ${item.type} "${item.name}"? This cannot be undone.`;
        if (window.confirm(confirmMsg)) {
            try {
                await deleteFile(currentSourceId, item.path, item.type === 'Directory');
                loadFiles(currentSourceId, currentPath);
            } catch (err: any) {
                console.error(err);
                alert("Failed to delete item: " + (err.response?.data || err.message));
            }
        }
    };

    const handleShare = async (e: React.MouseEvent, item: FileItem) => {
        e.stopPropagation();
        if (!currentSourceId) return;

        const params = new URLSearchParams();
        params.append('sourceId', currentSourceId.toString());
        params.append('path', item.path);

        const relativeUrl = `/api/files/download?${params.toString()}`;
        const absoluteUrl = `${window.location.origin}${relativeUrl}`;

        try {
            await navigator.clipboard.writeText(absoluteUrl);
            alert("Download link copied to clipboard!");
        } catch (err) {
            console.error('Failed to copy', err);
            alert("Failed to copy link");
        }
    };

    const handleDownload = (e: React.MouseEvent, item: FileItem) => {
        e.stopPropagation();
        if (!currentSourceId) return;

        // Construct URL
        const params = new URLSearchParams();
        params.append('sourceId', currentSourceId.toString());
        params.append('path', item.path);

        const url = `/api/files/download?${params.toString()}`;

        const link = document.createElement('a');
        link.href = url;
        link.download = item.name;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        link.remove();
    };

    const [showDuplicateCleaner, setShowDuplicateCleaner] = useState(false);

    const getFileIcon = (name: string) => {
        const ext = name.split('.').pop()?.toLowerCase();
        if (['mp3', 'flac', 'wav', 'm4a', 'ogg', 'opus'].includes(ext || '')) return <Music size={40} className="text-indigo-400" />;
        if (['mp4', 'mkv', 'mov', 'webm'].includes(ext || '')) return <Video size={40} className="text-purple-400" />;
        if (['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp'].includes(ext || '')) return <ImageIcon size={40} className="text-pink-400" />;
        if (['js', 'ts', 'tsx', 'jsx', 'json', 'html', 'css', 'py', 'cs', 'sh', 'md', 'yml', 'xml'].includes(ext || '')) return <FileCode size={40} className="text-emerald-400" />;
        return <FileText size={40} className="text-gray-400" />;
    };

    return (
        <div
            onDragEnter={handleDragEnter}
            className="fixed inset-0 bg-black/80 flex items-center justify-center z-50 p-4 animate-fade-in"
        >
            <DuplicateCleanerModal isOpen={showDuplicateCleaner} onClose={() => setShowDuplicateCleaner(false)} />
            <div
                className={`relative bg-gray-800 rounded-xl w-full max-w-4xl h-[80vh] flex flex-col shadow-2xl overflow-hidden border border-white/10 ${showDuplicateCleaner ? 'hidden' : ''}`}
            >

                {/* Header / Toolbar */}
                <div className="p-4 bg-gray-900 border-b border-white/10 flex flex-col gap-4 sticky top-0 z-10">
                    <div className="flex justify-between items-start">
                        {/* Left: Navigation Controls */}
                        <div className="flex flex-col gap-2 flex-1 mr-4 overflow-hidden">
                            <div className="flex items-center gap-3">
                                <button
                                    onClick={handleBack}
                                    disabled={history.length === 0}
                                    className="p-1.5 px-3 bg-gray-700 hover:bg-white/10 rounded disabled:opacity-30 text-white transition flex items-center justify-center"
                                    title="Go Back"
                                >
                                    <ArrowLeft size={18} />
                                </button>
                                <h2 className="text-lg font-bold text-white truncate">
                                    {currentSourceId ? (currentPath ? currentPath.split('/').pop() : 'Root') : 'Select Source'}
                                </h2>
                            </div>

                            {/* Path Label */}
                            {currentSourceId && (
                                <div className="text-xs text-indigo-300 font-mono bg-black/40 px-2 py-1 rounded truncate w-full flex items-center gap-2" title={`${currentBasePath}/${currentPath}`}>
                                    <HardDrive size={12} />
                                    <span>SMB Path: {currentBasePath}{currentPath ? (currentBasePath.endsWith('/') ? '' : '/') + currentPath : ''}</span>
                                </div>
                            )}
                        </div>

                        {/* Right: Actions */}
                        <div className="flex gap-2 shrink-0">
                            <button
                                onClick={() => setShowDuplicateCleaner(true)}
                                className="px-3 py-1.5 bg-orange-600/20 hover:bg-orange-600/30 text-orange-400 rounded text-sm transition border border-orange-500/20 flex items-center gap-2"
                                title="Find Duplicates"
                            >
                                <AlertTriangle size={16} />
                                <span className="hidden sm:inline">Clean Duplicates</span>
                            </button>
                            {currentSourceId && (
                                <>
                                    <button
                                        onClick={() => setShowNewFolderInput(!showNewFolderInput)}
                                        className="px-3 py-1.5 bg-gray-700 hover:bg-gray-600 rounded text-sm transition text-white border border-white/10 flex items-center gap-1"
                                    >
                                        <Plus size={16} /> Folder
                                    </button>
                                    <label className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 rounded text-sm cursor-pointer transition flex items-center gap-2 text-white shadow-lg shadow-indigo-500/20 px-4">
                                        <Upload size={16} />
                                        <span>Upload</span>
                                        <input type="file" className="hidden" multiple onChange={handleUpload} />
                                    </label>
                                </>
                            )}
                            <button onClick={onClose} className="p-1.5 hover:bg-red-500/20 text-gray-400 hover:text-red-400 rounded transition">
                                <X size={24} />
                            </button>
                        </div>
                    </div>

                    {/* New Folder Input Row */}
                    {showNewFolderInput && (
                        <div className="p-2 bg-gray-700/50 rounded flex gap-2 animate-fade-in border border-white/10">
                            <input
                                type="text"
                                className="flex-1 bg-gray-900 border border-gray-600 rounded px-3 py-1.5 text-sm outline-none focus:border-indigo-500 text-white"
                                placeholder="New Folder Name"
                                value={newFolderName}
                                onChange={e => setNewFolderName(e.target.value)}
                                autoFocus
                                onKeyDown={e => e.key === 'Enter' && handleCreateFolder()}
                            />
                            <button onClick={handleCreateFolder} className="px-4 py-1.5 bg-green-600 hover:bg-green-500 rounded text-sm text-white font-medium">Create</button>
                        </div>
                    )}

                    {/* Simple Progress Bar (Removed) */}
                </div>

                {/* File List */}
                <div className="flex-1 overflow-y-auto p-4 bg-gray-800/50">
                    {loading ? (
                        <div className="flex justify-center items-center h-full text-gray-400">Loading...</div>
                    ) : (
                        <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
                            {items.map((item, idx) => (
                                <div
                                    key={idx}
                                    onClick={() => handleNavigate(item)}
                                    className={`
                                            group relative p-4 bg-gray-700/30 rounded-lg hover:bg-gray-700/80 cursor-pointer transition border border-transparent hover:border-white/10 flex flex-col items-center gap-3 text-center
                                            ${item.type === 'File' ? 'opacity-75 hover:opacity-100' : ''}
                                        `}
                                >
                                    <div className="text-4xl">
                                        {item.type === 'Source' ? <HardDrive size={40} className="text-cyan-400" />
                                            : item.type === 'Directory' ? <Folder size={40} className="text-yellow-400" />
                                                : getFileIcon(item.name)}
                                    </div>
                                    <span className="text-sm text-gray-300 group-hover:text-white truncate w-full px-1" title={item.name}>
                                        {item.name}
                                    </span>

                                    {/* Action Buttons (Download/Delete) */}
                                    {item.type !== 'Source' && (
                                        <div className="absolute top-2 right-2 flex gap-1 opacity-0 group-hover:opacity-100 transition-all">
                                            {item.type === 'File' && (
                                                <>
                                                    <button
                                                        onClick={(e) => handleShare(e, item)}
                                                        className="p-1.5 bg-purple-600/80 hover:bg-purple-500 rounded-full text-white shadow-lg transform scale-90 hover:scale-100 transition"
                                                        title="Copy Link"
                                                    >
                                                        <Share2 size={14} />
                                                    </button>
                                                    <button
                                                        onClick={(e) => handleDownload(e, item)}
                                                        className="p-1.5 bg-blue-600/80 hover:bg-blue-500 rounded-full text-white shadow-lg transform scale-90 hover:scale-100 transition"
                                                        title="Download"
                                                    >
                                                        <Download size={14} />
                                                    </button>
                                                </>
                                            )}
                                            <button
                                                onClick={(e) => handleDelete(e, item)}
                                                className="p-1.5 bg-red-600/80 hover:bg-red-500 rounded-full text-white shadow-lg transform scale-90 hover:scale-100 transition"
                                                title="Delete"
                                            >
                                                <Trash2 size={14} />
                                            </button>
                                        </div>
                                    )}
                                </div>
                            ))}

                            {!loading && items.length === 0 && (
                                <div className="col-span-full text-center text-gray-500 py-20 flex flex-col items-center gap-2">
                                    <Folder size={48} className="opacity-20" />
                                    <span>Empty Folder</span>
                                </div>
                            )}
                        </div>
                    )}
                </div>
                {/* Drag Drop Overlay */}
                {isDragging && (
                    <div
                        onDragLeave={handleOverlayDragLeave}
                        onDrop={handleOverlayDrop}
                        onDragOver={(e) => e.preventDefault()}
                        className="absolute inset-0 z-50 bg-gray-900/90 border-4 border-indigo-500/50 border-dashed rounded-xl flex flex-col items-center justify-center gap-4 animate-fade-in backdrop-blur-sm"
                    >
                        <div className="p-6 bg-gray-800 rounded-full shadow-2xl animate-bounce">
                            <Upload size={48} className="text-indigo-400" />
                        </div>
                        <h3 className="text-2xl font-bold text-white">Drop files to upload</h3>
                        <p className="text-gray-400">Support multiple files</p>
                    </div>
                )}

                {/* Upload Status Overlay */}
                {uploadStatus.active && (
                    <div className="absolute inset-x-0 bottom-0 p-4 bg-gray-900/95 border-t border-indigo-500/30 flex flex-col gap-2 z-20 animate-slide-up shadow-2xl">
                        <div className="flex justify-between items-center text-white">
                            <span className="font-bold flex items-center gap-2">
                                <Upload size={18} className="text-indigo-400 animate-bounce" />
                                Uploading {uploadStatus.currentIndex} / {uploadStatus.total}
                            </span>
                            <button onClick={cancelUpload} className="text-xs px-2 py-1 bg-red-600/20 text-red-400 hover:bg-red-600/40 rounded border border-red-500/20">
                                Cancel
                            </button>
                        </div>
                        <div className="text-xs text-gray-400 truncate font-mono">
                            {uploadStatus.currentFile}
                        </div>
                        <div className="h-2 bg-gray-700 w-full rounded-full overflow-hidden">
                            <div className="h-full bg-gradient-to-r from-indigo-500 to-purple-500 transition-all duration-300" style={{ width: `${uploadStatus.progress}%` }} />
                        </div>
                        <div className="flex justify-between text-xs text-gray-500">
                            <span>{uploadStatus.progress}%</span>
                            {uploadStatus.failures.length > 0 && <span className="text-red-400">{uploadStatus.failures.length} Failed</span>}
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
};
