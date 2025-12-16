import React, { useState, useEffect, useRef } from 'react';
import { browseFiles, createDirectory, uploadFile, type FileItem } from '../services/files';
import { X, Folder, FileText, HardDrive, ArrowLeft, Upload, Plus } from 'lucide-react';

interface FileManagerProps {
    onClose: () => void;
}

export const FileManager: React.FC<FileManagerProps> = ({ onClose }) => {
    const [currentPath, setCurrentPath] = useState('');
    const [currentSourceId, setCurrentSourceId] = useState<number | undefined>(undefined);
    const [items, setItems] = useState<FileItem[]>([]);
    const [loading, setLoading] = useState(false);
    const [uploadProgress, setUploadProgress] = useState<number | null>(null);
    const [showNewFolderInput, setShowNewFolderInput] = useState(false);
    const [newFolderName, setNewFolderName] = useState('');

    const [currentBasePath, setCurrentBasePath] = useState('');

    // History Stack for Back Nav: { sid, path, basePath }
    const [history, setHistory] = useState<{ sid: number | undefined, path: string, basePath: string }[]>([]);

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
            // Use path provided by backend which should be relative to Source Root
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

    const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
        if (!e.target.files?.length || !currentSourceId) return;

        const file = e.target.files[0];
        setUploadProgress(0);

        try {
            await uploadFile(currentSourceId, currentPath, file, (pct: number) => setUploadProgress(pct));
            alert("Upload Complete!");
            loadFiles(currentSourceId, currentPath); // Refresh
        } catch (err) {
            console.error(err);
            alert("Upload Failed");
        } finally {
            setUploadProgress(null);
            // Clear input
            e.target.value = '';
        }
    };

    const handleCreateFolder = async () => {
        if (!newFolderName || !currentSourceId) return;

        try {
            // Path logic: currentPath is relative to Source Root.
            // Mkdir expects path relative to Share (handled by backend now via BaseDir prepending).
            // But API needs Relative Path (Frontend State).
            // Actually API needs whatever browse passed?
            // "path" param in browse is relative to Source.
            // So mkdir expects relative to Source.

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

    return (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center z-50 p-4 animate-fade-in">
            <div className="bg-gray-800 rounded-xl w-full max-w-4xl h-[80vh] flex flex-col shadow-2xl overflow-hidden border border-white/10">

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
                                        <input type="file" className="hidden" onChange={handleUpload} />
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

                    {/* Progress Bar */}
                    {uploadProgress !== null && (
                        <div className="h-1 bg-gray-700 w-full rounded-full overflow-hidden mt-1">
                            <div className="h-full bg-indigo-500 transition-all duration-300" style={{ width: `${uploadProgress}%` }} />
                        </div>
                    )}
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
                                                : <FileText size={40} className="text-gray-400" />}
                                    </div>
                                    <span className="text-sm text-gray-300 group-hover:text-white truncate w-full px-1" title={item.name}>
                                        {item.name}
                                    </span>
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
            </div>
        </div>
    );
};
