import React, { useState } from "react";
import {FileUpload} from "primereact/fileupload";
import {InputText} from "primereact/inputtext";
import {InputPanel} from "./InputPanel";
import {Button} from "primereact/button";

export function FileInput(props: {
    data: any,
    column: { field: string, header: string },
    register: any
    className: any
    control: any
    id: any
    uploadUrl: any
    previewImage?: boolean
    download?: boolean
    getFullAssetsURL: (arg: string) => string
    fileSelector?: React.ComponentType<{
        show: boolean;
        setShow: (show: boolean) => void;

        path: string;
        setPath: (paths: string) => void;
    }>
    
    metadataEditor?: React.ComponentType<{
        path:string;    
        show: boolean;
        setShow: (show: boolean) => void;
    }>
}) {
    const FileSelectDialog = props.fileSelector;
    const MetadataEditDialog = props.metadataEditor;
    const [showChooseLib,setShowChooseLib] = useState(false)
    const [showEditMetadata,setShowEditMetadata] = useState(false)

    return <InputPanel  {...props} childComponent={(field: any) => {
        const {uploadUrl} = props
        
        return <>
            <InputText 
                id={field.name} 
                value={field.value} 
                className={' w-full'}
                onChange={(e) => field.onChange(e.target.value)}
            />
            {
                field.value && props.previewImage && <img 
                    src={props.getFullAssetsURL(field.value)} alt={''} 
                    height={150}
                />
            }
            {field.value && props.download && <a href={props.getFullAssetsURL(field.value)}><h4>Download</h4></a>}
            <div style={{display: "flex", gap: "10px", alignItems: "center"}}>
                <FileUpload
                    withCredentials
                    mode={"basic"}
                    auto
                    url={uploadUrl}
                    onUpload={(e) => {
                        field.onChange(e.xhr.responseText);
                    }}
                    chooseLabel="Upload"
                    name={'files'}
                />
                {FileSelectDialog && (
                    <Button type='button'
                            icon={'pi pi-database'}
                            label="Choose"
                            onClick={()=>setShowChooseLib(true)}
                            className="p-button " // Match FileUpload styling
                    />
                )}
                {MetadataEditDialog && <Button type='button'
                        icon={'pi pi-pencil'}
                        label="Edit"
                        onClick={()=>setShowEditMetadata(true)}
                        className="p-button "
                />}
                <Button type='button'
                        icon={'pi pi-trash'}
                        label="Delete"
                        onClick={()=>field.onChange("")}
                        className="p-button " 
                />
            </div>
            {
                FileSelectDialog &&
                    <FileSelectDialog 
                        path={field.value}
                        setPath={path => field.onChange(path)}
                        show={showChooseLib}
                        setShow={setShowChooseLib}
                    />
            }
            {
                MetadataEditDialog &&
                <MetadataEditDialog
                    path={field.value}
                    show={showEditMetadata}
                    setShow={setShowEditMetadata}
                />
            }
        </>
    }}/>
}